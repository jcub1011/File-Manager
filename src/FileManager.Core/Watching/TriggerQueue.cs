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
/// <see cref="IPauseStateService.Subscribe"/>.
///
/// <para><b>Two independent pauses, with different shapes.</b> The GLOBAL pause
/// (<see cref="IPauseStateService"/>) shuts the dequeue gate outright — nothing is served. A PER-RUN pause
/// (<see cref="IRunPauseGate"/>) is a filter instead: the consumer skips past a paused run's payloads and
/// serves the next eligible one, so pausing one run does not stall every other. The queue stays FIFO among
/// eligible payloads, and a paused run's entries keep their positions, so resuming does not send it to the
/// back of the line.</para></summary>
public sealed class TriggerQueue : ITriggerQueue, IDisposable
{
    private readonly ILogger<TriggerQueue> _logger;
    private readonly IRunPauseGate _runPause;
    private readonly IDisposable _pauseSubscription;
    private readonly IDisposable _runPauseSubscription;

    private readonly object _gate = new();
    private readonly LinkedList<Payload> _queue = new();
    private readonly Dictionary<string, LinkedListNode<Payload>> _index = new(StringComparer.OrdinalIgnoreCase);

    // A wakeup the single consumer awaits when the queue is empty or the engine is paused. Signalled
    // (and swapped) by Enqueue and by a resume. Completing the exact TCS the consumer captured under
    // the lock closes the lost-wakeup race: waiting only happens when the queue was empty under the
    // lock, so any later Enqueue's signal completes the captured TCS.
    private TaskCompletionSource _wakeup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _paused;

    public TriggerQueue(IPauseStateService pauseState, ILogger<TriggerQueue> logger, IRunPauseGate? runPause = null)
    {
        ArgumentNullException.ThrowIfNull(pauseState);
        _logger = logger;
        // Optional so every existing test and any host without a run coordinator keeps constructing this
        // with two arguments and gets the never-paused behaviour it had before per-run pause existed.
        _runPause = runPause ?? NullRunPauseGate.Instance;
        _paused = pauseState.IsPaused;
        _pauseSubscription = pauseState.Subscribe(OnPauseChanged);
        // A resumed run may have payloads that were skipped while the consumer was parked on _wakeup, so
        // the same wake the global resume needs is needed here — without it a resume takes effect only
        // when the NEXT unrelated Enqueue happens to signal.
        _runPauseSubscription = _runPause.Subscribe(OnRunPauseChanged);
    }

    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public EnqueueOutcome Enqueue(Payload payload)
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
                // The displaced payload is REPORTED, not just dropped: if it belonged to a run that is
                // counting jobs, that run has to stop expecting one for it.
                Payload displaced = existing.Value;
                existing.Value = payload;
                return new EnqueueOutcome(Queued: false, Displaced: displaced);
            }
            LinkedListNode<Payload> node = _queue.AddLast(payload);
            _index[key] = node;
        }
        Wake();
        return new EnqueueOutcome(Queued: true, Displaced: null);
    }

    public int PendingCountForRun(Guid runId)
    {
        int count = 0;
        lock (_gate)
        {
            foreach (Payload pending in _queue)
                if (pending.RunId == runId)
                    count++;
        }
        return count;
    }

    public int DropRun(Guid runId)
    {
        int dropped = 0;
        lock (_gate)
        {
            LinkedListNode<Payload>? node = _queue.First;
            while (node is not null)
            {
                LinkedListNode<Payload>? next = node.Next;
                if (node.Value.RunId == runId)
                {
                    _index.Remove(KeyOf(node.Value));
                    _queue.Remove(node);
                    dropped++;
                }
                node = next;
            }
        }
        if (dropped > 0)
            _logger.LogInformation("Dropped {Count} pending payload(s) for cancelled run {RunId}", dropped, runId);
        return dropped;
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
                // FirstEligible, not First: a per-run pause filters rather than blocks, so the consumer
                // walks past a paused run's entries to the next payload it may serve. Null means "nothing
                // servable right now" — an empty queue and an all-paused one are the same wait.
                if (!_paused && FirstEligible() is { } head)
                {
                    _queue.Remove(head);
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

    /// <summary>The first pending payload the consumer may serve: the earliest one whose run is not
    /// individually paused. Must be called under <see cref="_gate"/>.
    ///
    /// <para>Walks rather than peeking, which is the cost of per-run pause and is bounded by the number of
    /// PAUSED entries ahead of the first eligible one — zero in the ordinary case, since
    /// <see cref="IRunPauseGate.IsRunPaused"/> short-circuits on <see cref="Guid.Empty"/> and nothing is
    /// paused. It is a linear scan only while a paused run has a long pending prefix, which is exactly the
    /// situation the user created on purpose.</para>
    ///
    /// <para>The gate is read here rather than cached per payload deliberately: a run's pause state can
    /// change between two dequeues, and the coordinator is the only authority on it.</para></summary>
    private LinkedListNode<Payload>? FirstEligible()
    {
        for (LinkedListNode<Payload>? node = _queue.First; node is not null; node = node.Next)
        {
            // A null RunId is a payload belonging to no run (a watcher or scheduler trigger), which no
            // per-run pause can withhold — so it is eligible without consulting the gate at all.
            if (node.Value.RunId is not Guid runId || !_runPause.IsRunPaused(runId))
                return node;
        }
        return null;
    }

    private void OnPauseChanged(bool paused)
    {
        _paused = paused;
        if (!paused)
            Wake();   // let the consumer re-check the gate
    }

    /// <summary>Re-checks the gate after a per-run pause transition. Only a RESUME can make a previously
    /// ineligible payload servable, so only a resume needs the wake — a pause takes effect on the
    /// consumer's next pass either way, and waking on it would be a spurious loop.</summary>
    private void OnRunPauseChanged(Guid runId, bool paused)
    {
        if (!paused)
            Wake();
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

    public void Dispose()
    {
        _pauseSubscription.Dispose();
        _runPauseSubscription.Dispose();
    }
}
