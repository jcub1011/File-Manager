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
