using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;

namespace FileManager.Contracts.IPC;

public sealed record EngineStatusSnapshot(
    bool Paused, int ActiveProfiles, int JobsInFlight, int QueuedPayloads, string? LastError)
{
    /// <summary>Full path of the executable this service is running from, as the service itself sees
    /// it (<c>Environment.ProcessPath</c>). Null when the host cannot determine it.
    /// <para>Reported so the UI can answer "am I talking to the executable the user configured?" —
    /// which it otherwise cannot, since a service that was already running (autostart, or a previous
    /// session) was never resolved by this client at all. The settings window needs the truth to
    /// decide whether changing the path means anything, and to name what it is offering to shut
    /// down. An init property rather than a positional member so existing constructions are
    /// unaffected.</para></summary>
    public string? ExecutablePath { get; init; }

    /// <summary>A problem found while the service was starting that leaves it running but degraded —
    /// today, profiles that could not be loaded. Null when startup was clean.
    /// <para>Carried on the SNAPSHOT rather than only as an <c>engine-warning</c> event because the
    /// event cannot reach anyone: it is published microseconds after the IPC server opens, and a UI
    /// that launched the service does not finish subscribing until well after that, so it is always
    /// dropped in the one flow that matters. Polled state has no such race — a client picks this up on
    /// its very first get-status, whenever it connects.</para>
    /// <para>Distinct from <see cref="LastError"/>, which is per-job health and is cleared by the next
    /// success. This one is a fact about the process and stays put for its lifetime.</para></summary>
    public string? StartupWarning { get; init; }
}

public sealed record ProfileSummary(
    Guid ProfileId, string Name, bool Active, string TriggerSummary);

public sealed record ProfileMatchDto(Guid ProfileId, string ProfileName, string MatchedSourceRoot);

/// <summary>Warning: informational, never blocks. BlockingWarning: blocks a save unless the
/// request sets AcknowledgeWarnings (spec §6.1's "blocking warning"). Error: always blocks.</summary>
public enum ValidationSeverity { Warning, BlockingWarning, Error }

public sealed record ValidationIssue(ValidationSeverity Severity, string Code, string Message);

public sealed record JobSummaryDto(
    Guid JobId, Guid ProfileId, string SourcePath, string Outcome,
    string? SkipReason, DateTimeOffset StartedAtUtc, TimeSpan Duration);

/// <summary>One run as a job-queue row: where it is, what it plans to do, and how much of that is done.
/// The wire form of Core's <c>RunStatus</c>, answered by <c>get-runs</c>.
///
/// <para><b>Why <see cref="ProfileName"/> is on the wire rather than resolved client-side.</b> Every other
/// run-shaped DTO carries only a <c>ProfileId</c> and lets the client look the name up in its profile
/// list. That cannot work here: a run planned from an unsaved draft has a profile that exists in no
/// catalog, so the lookup returns null and the row is permanently nameless. The coordinator holds the
/// frozen profile the run was planned against, so it is the only thing that can answer.</para>
///
/// <para><b>Snapshot semantics</b>, like <c>RunStatus</c> — read once, do not expect it to update. Live
/// movement arrives as <c>run-progress</c>; this is what a client re-seeds from.</para></summary>
/// <param name="Phase">The run's <c>RunPhase</c> as a string (<c>Planning</c>, <c>AwaitingApproval</c>,
/// <c>Executing</c>, <c>Closed</c>). A string for the same reason <c>RunProgressEvent.Phase</c> is one:
/// the phase is engine-internal and crossing the IPC boundary as a name keeps an added member from being
/// a breaking wire change.</param>
/// <param name="Outcome">The run's <c>RunOutcome</c> as a string. <c>None</c> until it closes.</param>
/// <param name="Paused">Whether THIS run is individually paused (see <see cref="SetRunPausedRequest"/>).
/// Independent of the global engine pause reported by <see cref="EngineStatusSnapshot.Paused"/>.</param>
/// <param name="Waiting">The run is in <c>Planning</c> but has not started walking — it is queued behind
/// the concurrent-plan limit. Distinguished from "planning slowly" deliberately: an unchanging caption
/// that could mean either is the exact complaint the planning-progress counts were added to fix.</param>
/// <param name="StartedAtUtc">When the run was created. Always set, unlike
/// <paramref name="PlannedAtUtc"/>, so it is what a queue orders by — a still-planning run has no plan
/// timestamp and would otherwise have no position.</param>
/// <param name="PlannedAtUtc">When planning finished and the work list was frozen — the age a client
/// measures staleness against. Null while still planning.</param>
/// <param name="ClosedAtUtc">When the run reached its terminal state; null while it is still live.
/// <para>A finished run is now retained until it is discarded or auto-deleted, so a queue has to be able
/// to say HOW finished — a run that ended a minute ago and one that ended yesterday are both
/// <c>Closed</c>. The terminal event carries the same instant, but only to a client that was listening at
/// the time; this is what lets a reconcile age a row it is seeing for the first time.</para></param>
/// <param name="BytesSettled">Source bytes the settled jobs accounted for, against
/// <paramref name="PlannedCopyBytes"/> — the byte counterpart of
/// <paramref name="Succeeded"/> + <paramref name="Skipped"/> + <paramref name="Failed"/>.
/// <para>Here as well as on <c>run-progress</c> because a queue re-seeds from this request on every
/// reconnect and on window open: without it a byte progress bar would reset to zero every time the
/// window was reopened mid-run, which reads as the run having restarted.</para>
/// <para>Last in the list, and defaulted, so the twenty existing positional arguments keep their
/// meaning.</para></param>
public sealed record RunSummaryDto(
    Guid RunId, Guid ProfileId, string ProfileName, string Phase, string Outcome,
    bool Paused, bool Waiting, DateTimeOffset StartedAtUtc, DateTimeOffset? PlannedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    int PlannedCopies, int PlannedDeletes, long PlannedCopyBytes, long PlannedDeleteBytes,
    int Succeeded, int Skipped, int Failed, int Deleted, bool PlanTruncated, string? PlanError,
    long BytesSettled = 0);

/// <summary>What one run is going to do, summarized — the answer to <see cref="GetRunDetailRequest"/>.
///
/// <para><b>The whole of this is read from the run's snapshot HEADER</b>, which is written once when
/// planning completes. No item file is opened and nothing is re-planned, which is what makes it cheap
/// enough to fetch on every selection change in a queue.</para>
///
/// <para><b>Snapshot semantics, and deliberately frozen.</b> Every figure here describes the plan as it
/// was when the run was planned — including <see cref="Profile"/>, which is the profile the run will
/// EXECUTE, not whatever the catalog holds now. A profile can be edited or deleted while its run waits for
/// approval, and a summary that showed the live version would describe work that is not going to
/// happen.</para></summary>
/// <param name="Profile">The profile the run was planned against, embedded whole. The source of the
/// sources/destinations lists and the read-only settings a client shows beside the plan. Comes from the
/// snapshot rather than a catalog lookup for two reasons: it is the frozen revision (see above), and a run
/// planned from an unsaved draft has a profile that is in no catalog at all.</param>
/// <param name="ScopePath">The single path the run was narrowed to, or null for the whole profile. Worth
/// surfacing because a narrowed run behaves differently in a way no count reveals: its orphan set cannot be
/// trusted over a partial source tree, so a Mirror run scoped to a subfolder copies but deletes
/// nothing.</param>
/// <param name="PlannedAtUtc">When the work list was frozen — what a client measures staleness from.</param>
/// <param name="CopyItemCount">Files the run will copy or update, and their total bytes: the executable
/// half, and the run's own progress denominator.</param>
/// <param name="DeleteItemCount">Orphans a <c>SyncMode.Mirror</c> run will move to the Recycle Bin, and
/// the bytes they hold. Always zero otherwise, and the most consequential pair here.</param>
/// <param name="SourceItemCount">Every source the plan LOOKED at, whatever it decided — which is not
/// <paramref name="CopyItemCount"/>: a filtered file and an already-identical one each produce no copy
/// item. This is the "files scanned" figure, and the reason an already-synchronized profile reads as "up to
/// date" rather than as "found nothing".</param>
/// <param name="DestinationItemCount">Destination paths the plan projected.</param>
/// <param name="OverwriteCount">The blast radius: destinations that will be overwritten, destinations that
/// will be written under a suffixed name because something was already there, and sources that will be
/// moved or deleted once copied. Zero for a run planned before the snapshot recorded them, which reads as
/// "not recorded".</param>
/// <param name="Truncated">The plan does not cover everything it was asked to. Copies may still run;
/// orphan deletion refuses outright, because an incomplete plan's orphan list cannot be trusted.</param>
/// <param name="SweepFaultDetail">The first Warning-severity sweep fault's message, which already names the
/// path. Non-null implies <paramref name="Truncated"/>.</param>
/// <param name="Space">The per-volume space projection — will it fit — or null when none was folded (a
/// truncated plan, whose totals over a partial graph would be unsound). The same
/// <see cref="SpaceProjection"/> a preview's terminal frame carries, so a client renders it with the
/// storage panel it already has.</param>
public sealed record RunDetailDto(
    Guid RunId, Profile Profile, string? ScopePath, DateTimeOffset PlannedAtUtc,
    int CopyItemCount, long CopyBytes, int DeleteItemCount, long DeleteBytes,
    int SourceItemCount, int DestinationItemCount,
    int OverwriteCount, int RenameCount, int DisposalCount,
    bool Truncated, string? SweepFaultDetail, SpaceProjection? Space)
{
    /// <summary>The Preview tab's whole-plan aggregates, or null for a snapshot that recorded none.
    /// <para>An init property rather than a seventeenth positional member: every caller that only wants
    /// the blast-radius numbers keeps compiling, and the preview's own concerns stay visibly grouped
    /// rather than trailing the run summary's.</para></summary>
    public RunPlanPreviewAggregates? Preview { get; init; }
}

/// <summary>What a WINDOWED preview cannot count for itself: the facet keys and their row counts, and the
/// status totals, over the whole plan.
///
/// <para>The client used to fold all of this while walking the rows it had just ingested, which was
/// affordable only because it ingested every row. A preview that holds a page at a time cannot — and a
/// facet bar counting only the rows on screen would be worse than none. These are folded during the plan's
/// own walk (<c>RunSnapshotStore</c>) and read out of the header in O(1).</para>
///
/// <para><b>No common root.</b> It is derived from the facet keys below, client-side, by the one
/// implementation of that rule (<c>DryRunPaths.CommonRoot</c>, which lives in the UI and cannot be
/// referenced from the service) — a second copy here would be free to disagree about UNC shares or
/// drive-spanning sets.</para></summary>
public sealed record RunPlanPreviewAggregates
{
    /// <summary>Source rows per Source root — the Sources tab's source facet.</summary>
    public IReadOnlyDictionary<string, int> SourceRowsByRoot { get; init; } = new Dictionary<string, int>();

    /// <summary>Destination operations per Target root, orphans included — both tabs' destination
    /// facet.</summary>
    public IReadOnlyDictionary<string, int> DestinationRowsByRoot { get; init; } = new Dictionary<string, int>();

    /// <summary>Destination operations per kind. By kind and not by chip name: which chip a kind belongs
    /// to is a display decision and stays in the client, beside the mapping the rows themselves render
    /// through.</summary>
    public IReadOnlyDictionary<OperationKind, int> DestinationRowsByKind { get; init; } =
        new Dictionary<OperationKind, int>();

    /// <summary>Sources the plan will not act on — filtered out, or already identical.</summary>
    public int UntouchedCount { get; init; }

    /// <summary>Sources the plan will copy.</summary>
    public int ProcessedCount { get; init; }
}
