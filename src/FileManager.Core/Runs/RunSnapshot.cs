using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Runs;

/// <summary>What a run is going to do, captured before it does any of it.
///
/// <para><b>Why a run has a snapshot at all.</b> Without one, every job independently re-derives its
/// own work at the moment it runs, so a run's behaviour drifts with the filesystem underneath it,
/// there is no denominator for progress, and nothing can be shown to the user for approval except an
/// intention. With one, the work list is frozen, the totals are exact, and — the reason this matters
/// most — the deletions a <see cref="SyncMode.Mirror"/> run performs are the very same set the
/// preview displayed, because they are literally read back from this file rather than recomputed.</para>
///
/// <para>The header is written once, after planning completes; the items stream to a sibling
/// NDJSON file as the plan is produced, so a 500,000-file run never holds its work list in
/// memory.</para></summary>
public sealed record RunSnapshotHeader
{
    public required Guid RunId { get; init; }

    /// <summary>The profile as it was when the run was planned, embedded whole.
    /// <para>Not a reference to the catalog, and not just an id: the profile may be edited — or
    /// deleted — while the run is awaiting approval or mid-execution, and what executes must be what
    /// the user approved. This is the same reasoning that makes the journal snapshot
    /// <c>PolicySnapshot</c> rather than re-reading the profile during recovery.</para></summary>
    public required Profile Profile { get; init; }

    /// <summary>The scope the plan covered: a path when the run was narrowed to one (a shell
    /// invocation on a subfolder), null for the whole profile. A narrowed run's work list covers only
    /// part of the source set, which is why Mirror deletion refuses to act on one — everything outside
    /// the scope would look like an orphan.</summary>
    public string? ScopePath { get; init; }

    public required DateTimeOffset PlannedAtUtc { get; init; }

    /// <summary>Copy items in <c>items.ndjsonl</c> — the exact number of payloads the run will
    /// enqueue, and therefore the barrier's target count and progress denominator.</summary>
    public required int CopyItemCount { get; init; }

    /// <summary>Deletion items in <c>items.ndjsonl</c>. Always zero outside
    /// <see cref="SyncMode.Mirror"/>: an AdditiveArchive plan classifies pre-existing destination
    /// files as Untouched, never as orphans.</summary>
    public required int DeleteItemCount { get; init; }

    /// <summary>Total bytes the copy items name, for the run's byte-level reporting.</summary>
    public required long CopyBytes { get; init; }

    /// <summary>Total bytes the deletion items name — what a Mirror run will reclaim.</summary>
    public required long DeleteBytes { get; init; }

    /// <summary>Set when the plan does not cover everything it was asked to (the source scan or the
    /// destination sweep hit a bound, or a target root could not be fully walked).
    /// <para><b>Load-bearing for safety.</b> A truncated plan's orphan set is unsound — a file that
    /// appears to have no source may well be written by a source the scan never reached — so a Mirror
    /// deletion phase must refuse outright. Copies remain safe to perform (copying a subset destroys
    /// nothing), so a truncated plan may still execute its copy half with a loud warning.</para></summary>
    public required bool Truncated { get; init; }

    /// <summary>The first Warning-severity sweep fault's message, which already names the path. For
    /// the user-facing notice; non-null implies <see cref="Truncated"/>.</summary>
    public string? SweepFaultDetail { get; init; }

    /// <summary>The space projection folded during planning, or null when the plan was truncated
    /// (totals over a partial graph would be unsound). Carried so the approval view can show "will it
    /// fit" without a second pass, and so the run records what it predicted.</summary>
    public SpaceProjection? Space { get; init; }
}

/// <summary>One file the run will copy. Deliberately thin: the executor re-screens and re-checks
/// unchanged when the job actually runs, so this carries what is needed to build the
/// <c>Payload</c> and to report totals — not a decision the executor must be made to honor.
/// <para><see cref="PlannedKind"/> is therefore <b>advisory</b>: it is what the plan predicted, used
/// for the approval display and for progress attribution. The executor remains authoritative, and
/// because it re-screens it can only ever do LESS than the plan predicted, never more. That asymmetry
/// is the safety property that lets the plan be trusted as a ceiling.</para></summary>
public sealed record RunCopyItem
{
    public required string SourcePath { get; init; }

    /// <summary>The Source root this file was found under — the profile's configured root, which is
    /// what <c>TargetPathLayout</c> makes the destination path relative to and what
    /// <c>JobPlanFactory.ResolveSourceIndex</c> resolves the M:1 priority rank from.</summary>
    public required string SourceRoot { get; init; }

    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }

    /// <summary>What the plan predicted would happen at the destination(s). Advisory — see the type
    /// doc. <see cref="OperationKind.Processed"/> for a source-side item whose per-target outcome the
    /// plan did not resolve to a single kind.</summary>
    public required OperationKind PlannedKind { get; init; }

    /// <summary>Which Source of the profile this file is being taken from, or -1 when its root matched
    /// none. Recorded rather than re-derived so a future source-selection strategy (preferring a
    /// faster volume when two Sources offer byte-identical copies) changes PLANNING only and never
    /// execution — see <c>ISourceSelector</c>.</summary>
    public required int SourceIndex { get; init; }
}

/// <summary>One destination file a <see cref="SyncMode.Mirror"/> run will remove, because no source
/// in the plan writes to it. Recycle Bin only — spec §3.1.1 forbids hard-deleting an orphan.
/// <para><see cref="SizeBytes"/> and <see cref="LastWriteUtc"/> are not bookkeeping: the deletion pass
/// re-stats each path under its lock and refuses to delete one whose last-write no longer matches,
/// because that means something changed the file after the user approved its removal.</para></summary>
public sealed record RunDeleteItem
{
    public required string Path { get; init; }

    /// <summary>The Target root the orphan sits under, for reporting and for the per-root ratio guard
    /// that refuses a pass which would remove most of a target tree.</summary>
    public required string TargetRoot { get; init; }

    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
}

/// <summary>A snapshot's items, as read back. The reader streams, so callers that only need the
/// deletion half never materialize the (far larger) copy half.</summary>
public sealed record RunSnapshotItems
{
    public required IReadOnlyList<RunCopyItem> Copies { get; init; }
    public required IReadOnlyList<RunDeleteItem> Deletes { get; init; }
}
