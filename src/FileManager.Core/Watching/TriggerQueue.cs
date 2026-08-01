using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Watching;

/// <summary>The single funnel for all payloads regardless of trigger (§4.2, spec §3.2.4). Coalesces
/// on <c>(ProfileId, SourcePath)</c> while pending, FIFO otherwise; the dequeue side blocks while the
/// engine is paused. One consumer (the orchestrator's loop, §8) calls <see cref="DequeueAsync"/>;
/// many producers call <see cref="Enqueue"/>. The pause gate re-arms from
/// <see cref="IPauseStateService.Subscribe"/>.</summary>
public sealed class TriggerQueue : ITriggerQueue, IDisposable
{
    private readonly ILogger<TriggerQueue> _logger;
    private readonly IDisposable _pauseSubscription;

    private readonly object _gate = new();
    private readonly LinkedList<Payload> _queue = new();
    private readonly Dictionary<string, LinkedListNode<Payload>> _index = new(StringComparer.OrdinalIgnoreCase);

    // A wakeup the single consumer awaits when the queue is empty or the engine is paused. Signalled
    // (and swapped) by Enqueue and by a resume. Completing the exact TCS the consumer captured under
    // the lock closes the lost-wakeup race: waiting only happens when the queue was empty under the
    // lock, so any later Enqueue's signal completes the captured TCS.
    private TaskCompletionSource _wakeup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _paused;

    public TriggerQueue(IPauseStateService pauseState, ILogger<TriggerQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(pauseState);
        _logger = logger;
        _paused = pauseState.IsPaused;
        _pauseSubscription = pauseState.Subscribe(OnPauseChanged);
    }

    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public void Enqueue(Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string key = KeyOf(payload);
        lock (_gate)
        {
            if (_index.TryGetValue(key, out LinkedListNode<Payload>? existing))
            {
                // Coalesce: one entry per (profile, source) while pending. Keep the FIFO position;
                // refresh to the newest payload (freshest metadata/trigger/timestamp). Availability
                // is unchanged, so no wakeup is needed.
                existing.Value = payload;
                return;
            }
            LinkedListNode<Payload> node = _queue.AddLast(payload);
            _index[key] = node;
        }
        Wake();
    }

    public async IAsyncEnumerable<Payload> DequeueAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            Payload? next = null;
            Task wait;
            lock (_gate)
            {
                if (!_paused && _queue.First is { } head)
                {
                    _queue.RemoveFirst();
                    _index.Remove(KeyOf(head.Value));
                    next = head.Value;
                    wait = Task.CompletedTask;
                }
                else
                {
                    wait = _wakeup.Task;   // captured under the lock — see the lost-wakeup note above
                }
            }

            if (next is not null)
            {
                yield return next;
                continue;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);   // OperationCanceledException ends the stream
        }
    }

    private void OnPauseChanged(bool paused)
    {
        _paused = paused;
        if (!paused)
            Wake();   // let the consumer re-check the gate
    }

    private void Wake()
    {
        TaskCompletionSource toComplete;
        lock (_gate)
        {
            toComplete = _wakeup;
            _wakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        toComplete.TrySetResult();
    }

    private static string KeyOf(Payload payload) =>
        payload.ProfileId.ToString("N") + "\0" + payload.SourcePath;

    public void Dispose() => _pauseSubscription.Dispose();
}
