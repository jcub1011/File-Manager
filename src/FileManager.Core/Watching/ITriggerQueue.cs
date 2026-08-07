using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Watching;

public interface ITriggerQueue
{
    /// <summary>Enqueues, or coalesces onto a pending entry for the same
    /// <c>(ProfileId, SourcePath)</c>, and reports which happened.
    /// <para>The return value exists for run-scoped consumers. Coalescing silently replaces a pending
    /// payload, so a run counting "how many payloads will actually produce a job" cannot just count its
    /// <c>Enqueue</c> calls: a coalesced payload produces none, and the entry it DISPLACED belonged to
    /// some run that must stop expecting a job for it or its completion barrier waits forever.</para></summary>
    EnqueueOutcome Enqueue(Payload payload);

    /// <summary>Back-pressure for a BULK producer: completes once fewer than
    /// <paramref name="highWaterMark"/> payloads are pending, parking the caller until then.
    /// <para>A producer with a handful of payloads (a watcher, the scheduler) has no reason to call
    /// this. A producer feeding a whole run's approved copy list does: each pending payload holds live
    /// path strings, and nothing else bounds how many a plan may name. The work list is already durable
    /// in the run snapshot, so only a working set of it needs to be resident.</para></summary>
    Task WaitForRoomAsync(int highWaterMark, CancellationToken ct);

    IAsyncEnumerable<Payload> DequeueAsync(CancellationToken ct = default);
    int PendingCount { get; }

    /// <summary>Discards every pending payload belonging to <paramref name="runId"/> and returns how
    /// many went. Used to cancel a run: work already in flight is never interrupted (I-ATOMIC-JOB),
    /// but work that has not started need not happen.</summary>
    int DropRun(Guid runId);

    /// <summary>How many pending payloads belong to <paramref name="runId"/>. Non-destructive, unlike
    /// <see cref="DropRun"/>. Lets a run tell "my work has not started yet" from "my work is in flight",
    /// which is what its completion barrier's quiescence backstop needs when the settle count cannot be
    /// reached.</summary>
    int PendingCountForRun(Guid runId);
}

/// <summary>What <see cref="ITriggerQueue.Enqueue"/> did.</summary>
/// <param name="Queued">False when the payload coalesced onto a pending entry instead of taking a
/// place of its own — so it will NOT produce a job of its own.</param>
/// <param name="Displaced">The payload that was already pending for this
/// <c>(ProfileId, SourcePath)</c> and has just been superseded, when there was one. It will not
/// produce a job either.</param>
public readonly record struct EnqueueOutcome(bool Queued, Payload? Displaced);
