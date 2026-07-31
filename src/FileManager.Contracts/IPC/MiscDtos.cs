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
