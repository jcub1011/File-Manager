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

    // The mirror image of _wakeup, for the other side of the pipe. _wakeup parks the CONSUMER when there
    // is nothing to serve; this parks a BULK PRODUCER when there is too much already pending. Null
    // whenever nobody is waiting, so an ordinary dequeue does not allocate or signal anything — the
    // cost is confined to the case that needs it.
    //
    // The case that needs it is a run's copy list. RunCoordinator.EnqueueCopies used to push a whole
    // snapshot's worth of payloads in one synchronous loop, which was bounded only by the plan's file
    // cap; with that cap gone it is bounded by nothing, and every pending payload is two live path
    // strings. See WaitForRoomAsync.
    private TaskCompletionSource? _room;
    private int _roomThreshold;

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
        Payload? displaced = null;
        bool becameEligible = false;
        lock (_gate)
        {
            if (_index.TryGetValue(key, out LinkedListNode<Payload>? existing))
            {
                // Coalesce: one entry per (profile, source) while pending. Keep the FIFO position;
                // refresh to the newest payload (freshest metadata/trigger/timestamp).
                // The displaced payload is REPORTED, not just dropped: if it belonged to a run that is
                // counting jobs, that run has to stop expecting one for it.
                displaced = existing.Value;
                existing.Value = payload;
                // AVAILABILITY IS NOT NECESSARILY UNCHANGED, which is what this branch used to assume when
                // it returned without waking. Once a per-run pause can filter the queue, coalescing an
                // ELIGIBLE payload onto a PAUSED run's entry makes that entry servable — and a consumer
                // parked on _wakeup has nothing else to tell it so, because the paused run is still paused
                // and OnRunPauseChanged never fires. The payload then waited for an unrelated Enqueue.
                becameEligible = !IsEligible(displaced) && IsEligible(payload);
            }
            else
            {
                LinkedListNode<Payload> node = _queue.AddLast(payload);
                _index[key] = node;
            }
        }
        // Outside the lock, like the queued path: a wake completes a TCS whose continuations run elsewhere.
        if (displaced is null || becameEligible)
            Wake();
        return displaced is null
            ? new EnqueueOutcome(Queued: true, Displaced: null)
            : new EnqueueOutcome(Queued: false, Displaced: displaced);
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
        TaskCompletionSource? room;
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
            // The other way the queue shrinks. A cancel that drops a run's backlog has to release a
            // producer parked on it, or the cancelling run's own enqueue loop waits for a drain that
            // just happened and will not happen again.
            room = TakeRoomSignalIfDrained();
        }
        room?.TrySetResult();
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
            TaskCompletionSource? room = null;
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
                    room = TakeRoomSignalIfDrained();
                }
                else
                {
                    wait = _wakeup.Task;   // captured under the lock — see the lost-wakeup note above
                }
            }

            // Outside the lock, like Wake(): completing a TCS runs continuations, and none of them
            // should observe this queue mid-dequeue.
            room?.TrySetResult();

            if (next is not null)
            {
                yield return next;
                continue;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);   // OperationCanceledException ends the stream
        }
    }

    /// <summary>Whether the consumer may serve this payload — that is, whether its run is not individually
    /// paused. Must be called under <see cref="_gate"/>.
    ///
    /// <para>A null <c>RunId</c> is a payload belonging to no run (a watcher or scheduler trigger), which no
    /// per-run pause can withhold, so it is eligible without consulting the gate at all. That check is what
    /// keeps an unpaused engine's queue walk free of gate calls; there is deliberately no
    /// <see cref="Guid.Empty"/> special case inside <see cref="IRunPauseGate.IsRunPaused"/> to lean on.</para>
    ///
    /// <para>Shared by <see cref="FirstEligible"/> and <see cref="Enqueue"/> so the dequeue filter and the
    /// coalesce wake cannot disagree about what "servable" means.</para></summary>
    private bool IsEligible(Payload payload) =>
        payload.RunId is not Guid runId || !_runPause.IsRunPaused(runId);

    /// <summary>The first pending payload the consumer may serve. Must be called under <see cref="_gate"/>.
    ///
    /// <para>Walks rather than peeking, which is the cost of per-run pause and is bounded by the number of
    /// PAUSED entries ahead of the first eligible one — zero in the ordinary case, since nothing is paused
    /// and <see cref="IsEligible"/> answers a run-less payload without a lookup. It is a linear scan only
    /// while a paused run has a long pending prefix, which is exactly the situation the user created on
    /// purpose.</para>
    ///
    /// <para>The gate is read here rather than cached per payload deliberately: a run's pause state can
    /// change between two dequeues, and the coordinator is the only authority on it.</para></summary>
    private LinkedListNode<Payload>? FirstEligible()
    {
        for (LinkedListNode<Payload>? node = _queue.First; node is not null; node = node.Next)
        {
            if (IsEligible(node.Value))
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

    /// <summary>Parks a bulk producer until the queue has drained enough to accept more.
    ///
    /// <para><b>Why the queue needs a producer side at all.</b> A run's approved copy list is pushed in
    /// one loop by <c>RunCoordinator.EnqueueCopies</c>, and every pending payload holds two path strings
    /// until a worker takes it. That was bounded by the plan's 500,000-file cap; the cap is gone,
    /// because a plan is the work list a run executes and shortening it made a wrong job. So the bound
    /// moved here, where it belongs: the work list stays on disk in the run snapshot (which is why the
    /// snapshot exists), and only a working set of it is ever resident.</para>
    ///
    /// <para><b>Hysteresis is the point of the second threshold.</b> Resuming the producer the instant
    /// one payload is dequeued would hand it back a slot at a time and make the whole loop lockstep with
    /// the workers. It resumes at half <paramref name="highWaterMark"/> instead, so the producer refills
    /// in batches while the workers keep draining.</para>
    ///
    /// <para>Cancellation is the caller's escape hatch and is load-bearing: a run PAUSED with a full
    /// queue of its own payloads cannot drain itself, so the producer would otherwise park until the
    /// resume. That is the correct behaviour — a paused run should not be filling memory — but only
    /// because a cancel or a shutdown can still get it out.</para>
    ///
    /// <para>Several producers may wait at once (concurrent runs). They share one signal and each
    /// re-checks after waking, so a losing racer simply waits again; <see cref="_roomThreshold"/> being
    /// last-writer-wins only shifts when a wake happens, never whether the check is correct.</para></summary>
    public async Task WaitForRoomAsync(int highWaterMark, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(highWaterMark, 1);
        int resumeAt = Math.Max(1, highWaterMark / 2);
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_queue.Count < highWaterMark)
                    return;
                _roomThreshold = resumeAt;
                _room ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _room.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The signal to complete when the queue has drained to the waiting producer's threshold,
    /// or null when there is nobody waiting or it has not drained far enough. Must be called under
    /// <see cref="_gate"/>, immediately after a removal; the caller completes it outside the lock.</summary>
    private TaskCompletionSource? TakeRoomSignalIfDrained()
    {
        if (_room is null || _queue.Count > _roomThreshold)
            return null;
        TaskCompletionSource signal = _room;
        _room = null;
        return signal;
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
