using System;

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
public sealed record RunSummaryDto(
    Guid RunId, Guid ProfileId, string ProfileName, string Phase, string Outcome,
    bool Paused, bool Waiting, DateTimeOffset StartedAtUtc, DateTimeOffset? PlannedAtUtc,
    int PlannedCopies, int PlannedDeletes, long PlannedCopyBytes, long PlannedDeleteBytes,
    int Succeeded, int Skipped, int Failed, int Deleted, bool PlanTruncated, string? PlanError);
