using Microsoft.Extensions.Logging;
using System;

namespace FileManager.Contracts.IPC;

public sealed record EngineStatusSnapshot(
    bool Paused, int ActiveProfiles, int JobsInFlight, int QueuedPayloads, string? LastError);

public sealed record ProfileSummary(
    Guid ProfileId, string Name, bool Active, string TriggerSummary);

public sealed record ProfileMatchDto(Guid ProfileId, string ProfileName, string MatchedSourceRoot);

public sealed record ValidationIssue(LogLevel Severity, string Code, string Message);

public sealed record JobSummaryDto(
    Guid JobId, Guid ProfileId, string SourcePath, string Outcome,
    string? SkipReason, DateTimeOffset StartedAtUtc, TimeSpan Duration);
