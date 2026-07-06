using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.IPC;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JobStartedEvent), "job-started")]
[JsonDerivedType(typeof(JobCompletedEvent), "job-completed")]
[JsonDerivedType(typeof(JobFailedEvent), "job-failed")]
[JsonDerivedType(typeof(PauseChangedEvent), "pause-changed")]
[JsonDerivedType(typeof(ProfilesChangedEvent), "profiles-changed")]
[JsonDerivedType(typeof(EngineWarningEvent), "engine-warning")]
public abstract record EngineEvent
{
    public required DateTimeOffset AtUtc { get; init; }
}

public sealed record JobStartedEvent : EngineEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string SourcePath { get; init; }
}
public sealed record JobCompletedEvent : EngineEvent { public required JobSummaryDto Job { get; init; } }
public sealed record JobFailedEvent : EngineEvent
{
    public required JobSummaryDto Job { get; init; }
    public required string Error { get; init; }
    /// <summary>The profile's Logging.NotifyOnFailure, stamped by the service at publish time so
    /// the Contracts-only tray can decide whether to raise a native notification (spec §7).</summary>
    public required bool NotifyOnFailure { get; init; }
    /// <summary>Non-empty when rollback itself failed — paths needing manual remediation.</summary>
    public IReadOnlyList<string> ResidualPaths { get; init; } = [];
}
public sealed record PauseChangedEvent : EngineEvent { public required bool Paused { get; init; } }
public sealed record ProfilesChangedEvent : EngineEvent;
public sealed record EngineWarningEvent : EngineEvent { public required string Message { get; init; } }
