using System;
using System.Collections.Generic;

namespace FileManager.Core;

public sealed record EngineConfig
{
    public int MaxWorkers { get; init; } = System.Environment.ProcessorCount;
    public long PreflightSafetyMarginBytes { get; init; } = 64L * 1024 * 1024;  // 64 MiB
    public string? TempRoot { get; init; }                     // default: <root>\work
    public IReadOnlyList<string>? ExecutableAllowlist { get; init; }  // null = any existing file (spec §9)
    public bool LaunchTrayOnStart { get; init; } = true;
    public long JournalRotateAtBytes { get; init; } = 4L * 1024 * 1024;         // 4 MiB

    /// <summary>How many runs may be WALKING their plan at once. Surplus runs queue and report the
    /// <c>Waiting</c> phase; the slot is released when the work list is frozen, not when the run closes,
    /// so runs parked awaiting approval never hold one.
    ///
    /// <para>A safety valve rather than a throughput knob. Concurrent previews are deliberate — that is
    /// what the job queue is for — but planning is the memory-hungry half of a run: the service was
    /// measured at ~292 MB producing ONE 33,449-file plan, so an unbounded fan-out of large profiles
    /// exhausts it. Three is enough that concurrency is real and the ceiling is roughly a gigabyte in the
    /// worst case.</para>
    ///
    /// <para>Note this does NOT bound scan threads — <c>IScanScheduler</c> already applies a global and a
    /// per-drive budget across every walk. This bounds the number of plan-sized working sets alive at
    /// once.</para></summary>
    public int MaxConcurrentPlans { get; init; } = 3;

    /// <summary>Hard cap on how many FINISHED runs the coordinator retains, evicting the oldest-closed
    /// first. Live runs are never candidates.
    ///
    /// <para>A backstop, not a knob — which is why it lives here and not in <c>GlobalSettings</c>. The
    /// user-facing controls are "auto-delete finished runs" and "after how long", and the first of those
    /// can be switched OFF; without this cap that setting would be an unbounded allocation on a service
    /// that runs for months. It is set far above any hand-driven session, so in practice a run leaves the
    /// queue because the user discarded it or because auto-delete reaped it.</para>
    ///
    /// <para>500 matches <c>IJobLogStore</c>'s recent-jobs ring, and is affordable only because
    /// <c>RunState.ReleaseAfterClose</c> reduces a finished run to a handful of scalars — before that, a
    /// retained run held its whole profile plus one string per file it copied.</para></summary>
    public int MaxRetainedClosedRuns { get; init; } = 500;

    // ---- Mirror deletion pass (§10.1) -----------------------------------------------------------
    // Every one of these has a fail-closed default: when a bound is hit, NOTHING is deleted. None is
    // user-editable yet (EngineConfig is registered with defaults only, like the rest of this type).

    /// <summary>How long a run's deletion phase waits for its copy jobs to settle before giving up.
    /// On expiry the pass ABORTS and deletes nothing — never "delete anyway on timeout".</summary>
    public TimeSpan MirrorBarrierTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the queue and the in-flight count must both read empty before the barrier is
    /// treated as met despite an unreached settle count. The backstop for an accounting hole (a
    /// coalesced or dropped payload) that would otherwise stall to the timeout.</summary>
    public TimeSpan MirrorQuiescenceWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the pass waits for one orphan's path lock. <c>PathLockRegistry.AcquireAsync</c>
    /// has no timeout of its own, so the pass imposes one; exceeding it means a live job owns the path,
    /// and the orphan is SKIPPED rather than waited on.</summary>
    public TimeSpan MirrorLockWaitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on orphans one pass will remove. Crossing it aborts the pass entirely: a
    /// cap that deletes the first N and abandons the rest is worse than one that refuses and explains.</summary>
    public int MirrorMaxOrphans { get; init; } = 50_000;

    /// <summary>Refuse the pass when orphans would account for more than this fraction of the files
    /// swept under a single target root.
    /// <para>This is the gate that catches the two mistakes every other gate reports green for:
    /// flipping <c>TargetLayout</c> between PreserveStructure and Flatten (which makes every existing
    /// destination file an orphan), and a source share that remounted empty. Both would otherwise wipe
    /// a whole archive.</para></summary>
    public double MirrorMaxDeleteFraction { get; init; } = 0.5;

    /// <summary>Orphan count below which <see cref="MirrorMaxDeleteFraction"/> does not apply, so a
    /// small target root that legitimately loses most of its handful of files is not refused. (A root
    /// holding 3 files where 2 are orphans is 67% and entirely normal.)</summary>
    public int MirrorRatioFloor { get; init; } = 20;

    /// <summary>Consecutive write-ahead journal failures that end the pass mid-way. Past this the
    /// journal is effectively gone, and continuing would delete files without a durable record of
    /// having done so.</summary>
    public int MirrorMaxConsecutiveJournalFailures { get; init; } = 3;
}
