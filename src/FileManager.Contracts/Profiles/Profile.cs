using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.Profiles;

public sealed record Profile
{
    /// <summary>
    /// The version of this schema.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// The unique identifier of this profile.
    /// </summary>
    [JsonPropertyName("ProfileId")]
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required bool Active { get; init; }
    public required SyncMode SyncMode { get; init; }
    public required TargetLayout TargetLayout { get; init; }
    public required TriggerSettings Triggers { get; init; }
    public required IReadOnlyList<SourceConfig> Sources { get; init; }
    public IReadOnlyList<TransformerStep>? Transformers { get; init; }
    public required IReadOnlyList<TargetConfig> Targets { get; init; }
    public required PolicySettings Policies { get; init; }
    public FilterSet? Filters { get; init; }
    public required LoggingSettings Logging { get; init; }
}

public sealed record TriggerSettings
{
    public required bool ManualShell { get; init; }
    public required bool Watcher { get; init; }
    public ScheduleSettings? Schedule { get; init; }
}

public sealed record ScheduleSettings
{
    public required bool Enabled { get; init; }
    public required string Cron { get; init; }
    public required string Timezone { get; init; }                // IANA or Windows ID; default system-local
    public required MissedRunPolicy MissedRunPolicy { get; init; }
}

public sealed record SourceConfig
{
    public required string Path { get; init; }                    // absolute, machine-specific
    public int SettleDelaySeconds { get; init; } = 2;
    public int StabilityIntervalMs { get; init; } = 500;
    public FilterSet? Filters { get; init; }                      // overrides profile-global, field-by-field
}

public sealed record TransformerStep
{
    public required int Step { get; init; }                       // 1-based, contiguous
    public required string Name { get; init; }
    public required string ExecutablePath { get; init; }
    public required ArgumentMode ArgumentMode { get; init; }
    public required string Arguments { get; init; }
    public required OutputMode OutputMode { get; init; }
    public string? ExpectedOutputExtension { get; init; }         // required when OutputMode = NewFile
    public IReadOnlyList<int>? SuccessExitCodes { get; init; }    // default [0]
    public required int TimeoutSeconds { get; init; }
}

public sealed record TargetConfig
{
    public required string Path { get; init; }
}

public sealed record PolicySettings
{
    public required ConflictResolution ConflictResolution { get; init; }
    public required OverwriteHandling OverwriteHandling { get; init; }
    public required VerificationMethod VerificationMethod { get; init; }
    public required OnSuccessAction OnSuccess { get; init; }
    public string? ArchiveFolder { get; init; }                   // required when OnSuccess = MoveToArchive
    public required OnFailureAction OnFailure { get; init; }
    public required MetadataOnConflict MetadataOnConflict { get; init; }
}

public sealed record FilterSet
{
    public IReadOnlyList<string>? Include { get; init; }          // globs
    public IReadOnlyList<string>? ExcludeGlob { get; init; }
    public IReadOnlyList<string>? IncludeRegex { get; init; }
    public IReadOnlyList<string>? ExcludeRegex { get; init; }
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public TimeSpan? ModifiedWithin { get; init; }
    public TimeSpan? ModifiedOlderThan { get; init; }
    public TimeSpan? CreatedWithin { get; init; }
    public AttributeFilterSettings? Attributes { get; init; }
    public int? MaxDepth { get; init; }
    public bool ContentHashDedupe { get; init; }                  // [reserved — must be false in v1]
}

public sealed record AttributeFilterSettings
{
    public bool IncludeHidden { get; init; }
    public bool IncludeSystem { get; init; }
    public bool FollowSymlinks { get; init; }
}

public sealed record LoggingSettings
{
    public required LogLevel LogLevel { get; init; }
    public required bool NotifyOnFailure { get; init; }
}

public enum SyncMode
{
    AdditiveArchive,
    Mirror,               /// [reserved — fails v1 validation]
}

public enum TargetLayout { PreserveStructure, Flatten }

public enum ConflictResolution { Overwrite, OverwriteIfNewer, RenameSuffix, Skip }

public enum OverwriteHandling { DirectOverwrite, StageOverwrites }

public enum VerificationMethod
{
    [JsonStringEnumMemberName("SHA256")]
    Sha256,
    None,
    SizeTimestamp,        /// [reserved — fails v1 validation]
}

public enum OnSuccessAction { KeepSource, MoveToTrash, MoveToArchive, PermanentDelete }

public enum OnFailureAction { AbortRestoreAndClean }   // single value; extension point (spec §5.1)

public enum MetadataOnConflict { WarnAndContinue, FailJob }

public enum ArgumentMode
{
    Literal,
    Shell,                /// [reserved — fails v1 validation]
}

public enum OutputMode { NewFile, InPlace }

public enum MissedRunPolicy { CatchUpOnce, Skip }

public enum LogVerbosity { FailuresOnly, FailuresAndSkips, All }