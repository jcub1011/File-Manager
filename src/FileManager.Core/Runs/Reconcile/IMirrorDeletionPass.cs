using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Runs.Reconcile;

/// <summary>The Mirror reconcile pass: removes destination files no source writes to (spec §3.1.1),
/// to the Recycle Bin. Run-scoped rather than file-scoped — the per-file job model cannot express a
/// deletion, because a deletion is not driven by a file arriving.
/// <para>Never throws for a pass failure; every outcome, including an outright refusal, is a
/// <see cref="MirrorDeletionResult"/>.</para></summary>
public interface IMirrorDeletionPass
{
    Task<MirrorDeletionResult> DeleteAsync(MirrorDeletionRequest request, CancellationToken ct = default);
}

/// <summary>Everything the pass needs, fully resolved by the caller. Immutable: a profile edit or a
/// filesystem change mid-pass must not alter what this pass removes.</summary>
public sealed record MirrorDeletionRequest
{
    /// <summary>The pass's own identity. Reuses <see cref="JobId"/> so the pass is narrated through the
    /// existing per-job log and is drillable with <c>get-job-log</c> at no extra cost.</summary>
    public required JobId PassId { get; init; }

    /// <summary>The run this pass belongs to.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The profile AS PLANNED — read back from the run's snapshot, not from the catalog, so a
    /// profile edited while the run was awaiting approval cannot change what gets deleted.</summary>
    public required Profile Profile { get; init; }

    /// <summary>The orphans to remove, read back from the run's snapshot. This is the same set the
    /// preview displayed and the user approved; the pass computes nothing of its own.
    ///
    /// <para><b>Enumerated exactly once, and lazily.</b> It used to be an <c>IReadOnlyList</c>, which
    /// meant the caller materialized every orphan before the pass could even decide whether to refuse —
    /// the one snapshot consumer that did not stream, and unbounded now that a plan has no file cap.
    /// Everything the pass needs BEFORE the loop is a scalar the plan already counted, so those come in
    /// beside this rather than out of it: <see cref="OrphanCount"/>, <see cref="OrphanBytes"/> and
    /// <see cref="OrphansByTargetRoot"/>. Implementations must not enumerate this more than once.</para></summary>
    public required IEnumerable<RunDeleteItem> Orphans { get; init; }

    /// <summary>How many orphans <see cref="Orphans"/> will yield, from the snapshot header. Separate
    /// from the sequence because every use of it — the nothing-to-do check, the opened record, the log
    /// line — happens before the sequence may be walked.</summary>
    public required int OrphanCount { get; init; }

    /// <summary>The bytes those orphans hold, from the snapshot header, for the same reason as
    /// <see cref="OrphanCount"/>. Counted during the plan's own walk, so it costs nothing here.</summary>
    public required long OrphanBytes { get; init; }

    /// <summary>Orphans per target root — the ratio guard's numerator, against
    /// <see cref="SweptFilesByTargetRoot"/> as its denominator. Tallied by the plan alongside the
    /// denominator (same walk, same pass), so the guard can run before a single orphan is read.</summary>
    public required IReadOnlyDictionary<string, int> OrphansByTargetRoot { get; init; }

    /// <summary>Non-null when the run was narrowed to a path rather than covering the whole profile.
    /// Any value at all refuses the pass: a narrowed plan's orphan list would include everything
    /// outside the scope.</summary>
    public string? ScopePath { get; init; }

    /// <summary>The plan could not fully walk a tree, so its orphan list is a guess. Refuses the pass.
    /// <para>Once also set by a file-count bound; that bound is gone, and a sweep fault is now the only
    /// thing that raises this.</para></summary>
    public required bool PlanTruncated { get; init; }

    /// <summary>Part of the source or destination tree could not be read (an unreadable subdirectory,
    /// the depth ceiling). Refuses the pass — deliberately stricter than the dry run, which merely
    /// flags such a report as truncated. A preview may be incomplete; a deletion may not.</summary>
    public required bool EnumerationIncomplete { get; init; }

    /// <summary>How many of the run's copy jobs ended Failed or RollbackFailed. Any at all refuses the
    /// pass: until every copy landed, the destination is not yet a mirror of the source, and removing
    /// the old copy of a file whose replacement never arrived is data loss.
    /// <para>Note that a job's <c>DispositionError</c> is deliberately NOT counted here. Disposition
    /// concerns the SOURCE file after a successful copy; the destination copies are proven placed and
    /// verified, which is the only thing a deletion decision depends on.</para></summary>
    public required int CopyJobsFailed { get; init; }

    /// <summary>Every destination path this run actually wrote to, as reported by the copy jobs
    /// themselves. The structural guard against plan drift: conflict resolution may resolve a
    /// different final path at execution time than the plan's probe predicted, and such a path would
    /// otherwise look like an orphan the run had just created. Compared with
    /// <c>NormalizedPath</c>-equivalent casing.</summary>
    public required IReadOnlySet<string> PathsWrittenByThisRun { get; init; }

    /// <summary>How many files the plan's sweep saw under each target root, for the ratio guard. Keyed
    /// by the configured target root string.</summary>
    public required IReadOnlyDictionary<string, int> SweptFilesByTargetRoot { get; init; }
}

/// <summary>One orphan the pass could not remove, and why. Distinct from a skip: a skip is a
/// deliberate refusal to touch a path, a failure is an attempt that did not work.</summary>
public readonly record struct MirrorDeletionFailure(string Path, string Reason);

public sealed record MirrorDeletionResult
{
    public required JobId PassId { get; init; }
    public required MirrorReconcileOutcome Outcome { get; init; }
    public int Deleted { get; init; }

    /// <summary>Orphans not removed, for any reason — both deliberate skips and outright failures.</summary>
    public int Skipped { get; init; }

    public long BytesDeleted { get; init; }

    /// <summary>Why nothing, or nothing further, was deleted. Non-null on either aborted outcome, and
    /// phrased for the user because it rides out on an engine warning.</summary>
    public string? AbortReason { get; init; }

    public IReadOnlyList<MirrorDeletionFailure> Failures { get; init; } = [];
}
