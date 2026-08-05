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

    /// <summary>Copy items in <c>copies.ndjsonl</c> — the exact number of payloads the run will
    /// enqueue, and therefore the barrier's target count and progress denominator.</summary>
    public required int CopyItemCount { get; init; }

    /// <summary>Deletion items in <c>deletes.ndjsonl</c>. Always zero outside
    /// <see cref="SyncMode.Mirror"/>: an AdditiveArchive plan classifies pre-existing destination
    /// files as Untouched, never as orphans.</summary>
    public required int DeleteItemCount { get; init; }

    /// <summary>Total bytes the copy items name, for the run's byte-level reporting.</summary>
    public required long CopyBytes { get; init; }

    /// <summary>Total bytes the deletion items name — what a Mirror run will reclaim.</summary>
    public required long DeleteBytes { get; init; }

    /// <summary>Items in <c>sources.ndjsonl</c> and <c>destinations.ndjsonl</c>: the plan as it is SHOWN —
    /// every source it looked at, and every destination it projected. Display only; no phase of execution
    /// reads either file.
    /// <para>Both are zero for a snapshot written before these existed, which reads as "no projection
    /// recorded" and renders as a plan with nothing to display. That is exactly what such a snapshot
    /// contains, so the absence is honest rather than a parse failure.</para></summary>
    public int SourceItemCount { get; init; }

    public int DestinationItemCount { get; init; }

    /// <summary>Pre-existing files the destination sweep classified under each target root — survivors
    /// and orphans together. The denominator for <c>MirrorDeletionPass</c>'s ratio guard.
    /// <para>Recorded here because the sweep is the only place it can be counted: it sees each
    /// destination file exactly once. It used to be reconstructed downstream from
    /// <see cref="CopyItemCount"/> on the premise that the non-orphans are the files the copy items
    /// account for — false, because an already-identical file is <c>SkippedUnchanged</c> and yields no
    /// copy item. A synchronized profile's denominator therefore collapsed to its own orphan count and
    /// the guard refused every steady-state pass at 100%.</para>
    /// <para>Empty for a snapshot written before this existed. The guard skips a root it has no count
    /// for, which is the pre-existing behaviour for an unknown root.</para></summary>
    public IReadOnlyDictionary<string, int> SweptFilesByTargetRoot { get; init; } =
        new Dictionary<string, int>();

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
    /// none. Recorded rather than re-derived, which is what keeps the snapshot and the audit trail in
    /// agreement with reality once a source-selection strategy exists — the intended one (dispatching
    /// contested files by least outstanding read work per volume) decides at dispatch time, so this field
    /// reports what actually happened rather than what was predicted. Design in
    /// <c>docs/mirror-run-next-steps.md</c> §8–§9.</summary>
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

/// <summary>One source file the plan looked at, and what it decided about it — <b>including the ones it
/// decided to do nothing with</b>.
///
/// <para><b>Display only</b>, and deliberately NOT the same set as <see cref="RunCopyItem"/>. A copy item
/// exists only where there is work: a file the filters excluded, or one already identical at every target,
/// produces none. That is right for execution and wrong for display — a Mirror profile that is already up
/// to date has no copies at all, so a view built from the copy list shows an empty source panel, which
/// reads as "the preview found nothing" rather than "everything is already up to date".</para>
///
/// <para>Written for every scanned source in plan order, so an item's ordinal here IS the source index the
/// plan assigned it, and a <see cref="RunDestinationItem.SourceOrdinal"/> resolves against it
/// directly.</para></summary>
public sealed record RunSourceItem
{
    public required string Path { get; init; }

    /// <summary>The Source root this file was found under — the facet key, and what the relative-path
    /// alignment between the two tabs is computed against.</summary>
    public required string SourceRoot { get; init; }

    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }

    /// <summary>What the plan decided: Processed, SkippedByFilter, or SkippedUnchanged.</summary>
    public required OperationKind Kind { get; init; }

    /// <summary>What happens to the original after a successful copy. Null when the file does not
    /// process, which is what keeps a skipped file out of the disposal count.</summary>
    public OnSuccessAction? Disposition { get; init; }

    /// <summary>Display string carried through verbatim: the deciding filter rule, or the unchanged
    /// reason.</summary>
    public string? Detail { get; init; }
}

/// <summary>One destination path the plan projected, and what it projected for it: where a copy lands,
/// or a pre-existing file the run leaves alone.
///
/// <para><b>Display only.</b> Nothing in execution reads these — <c>JobPlanFactory</c> re-resolves every
/// destination from the profile and the executor re-checks each one, which is what keeps the plan a
/// ceiling rather than an instruction. They exist because the approval view IS the dry-run view: without
/// them the Preview tab can say WHICH files a run reads and nothing at all about where they go, and its
/// overwrite and rename counts — the blast radius the whole view is for — read zero.</para>
///
/// <para><b>Orphans are not here.</b> A <see cref="OperationKind.Deleted"/> destination goes to
/// <see cref="RunDeleteItem"/> instead, so the deletion pass still reads only the small half it needs and
/// no orphan is counted twice by a client reading both.</para></summary>
public sealed record RunDestinationItem
{
    /// <summary>The resulting destination path — which may not exist yet (a New or renamed path).</summary>
    public required string Path { get; init; }

    /// <summary>The Target root this path sits under: the facet key, and what the relative-path
    /// alignment between the two tabs is computed against.</summary>
    public required string TargetRoot { get; init; }

    /// <summary>What the plan projected for this path. Unlike <see cref="RunCopyItem.PlannedKind"/> this
    /// is per-destination and therefore meaningful: New / Overwrite / Rename / SkippedUnchanged /
    /// SkipConflict / Untouched / Unknown.</summary>
    public required OperationKind Kind { get; init; }

    /// <summary>Which source file's content lands here — its ordinal in <c>sources.ndjsonl</c>, which is
    /// the source index the plan assigned it — or <c>-1</c> for a destination no source writes into (a
    /// pre-existing file the sweep found, or the original kept beside a renamed copy).
    /// <para>Resolved against the source file, NOT the copy list: an unchanged file still projects a
    /// destination (that is what keeps its target off the orphan list) while producing no copy item at
    /// all, so a copy-relative index could not name it.</para></summary>
    public required int SourceOrdinal { get; init; }

    /// <summary>Display string carried through verbatim: the existing file's mtime, the suffixed rename
    /// name, or the unchanged reason.</summary>
    public string? Detail { get; init; }

    /// <summary>The pre-existing file this destination acts on, when there is one — its own path, which
    /// for a rename is NOT <see cref="Path"/> (the rename's path is the suffixed new name, its subject is
    /// the file that forced the suffix). Null means nothing is there yet.</summary>
    public string? SubjectPath { get; init; }

    public long? SubjectSizeBytes { get; init; }
    public DateTimeOffset? SubjectLastWriteUtc { get; init; }
}

/// <summary>A snapshot's items, as read back. The reader streams, so callers that only need the
/// deletion half never materialize the (far larger) copy half.</summary>
public sealed record RunSnapshotItems
{
    public required IReadOnlyList<RunCopyItem> Copies { get; init; }
    public required IReadOnlyList<RunDeleteItem> Deletes { get; init; }
}
