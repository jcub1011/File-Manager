using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FileManager.Contracts;

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

    /// <summary>Dry-run preview only: when true, destination roots are swept to list pre-existing
    /// files (Untouched in AdditiveArchive, Deleted orphans in Mirror). Mirror always sweeps
    /// regardless of this flag. Optional/additive: absent in legacy JSON deserializes to false.</summary>
    public bool ScanDestination { get; init; }

    /// <summary>The single source of truth for "does a dry run sweep the destination roots": Mirror
    /// always sweeps (its only source of Deleted-orphan previews), AdditiveArchive honors
    /// <see cref="ScanDestination"/>. Consumed by the engine and both dry-run handlers; the static
    /// form lets UI layers apply the same rule to transient (not-yet-saved) editor state.</summary>
    public static bool ComputeEffectiveScanDestination(SyncMode mode, bool scanDestination) =>
        mode == SyncMode.Mirror || scanDestination;

    [JsonIgnore]
    public bool EffectiveScanDestination => ComputeEffectiveScanDestination(SyncMode, ScanDestination);

    public required TargetLayout TargetLayout { get; init; }
    public required TriggerSettings Triggers { get; init; }
    public required IReadOnlyList<SourceConfig> Sources { get; init; }
    public IReadOnlyList<TransformerStep>? Transformers { get; init; }
    public required IReadOnlyList<TargetConfig> Targets { get; init; }
    public required PolicySettings Policies { get; init; }
    public FilterSet? Filters { get; init; }
    public required LoggingSettings Logging { get; init; }

    private readonly ConcurrencyOverride? _concurrency;

    /// <summary>Per-profile override for dry-run evaluation concurrency. Never null: a profile
    /// whose JSON predates this field deserializes with no value (the source generator does not run
    /// property initializers for absent members), which the getter reads back as
    /// <see cref="ConcurrencyOverride.Default"/> (<see cref="ConcurrencyMode.Inherit"/> — defer to
    /// the global setting). The setter collapses an explicit default back to the absent (null)
    /// representation so that a profile that omits the field and one that sets it to the default
    /// compare equal under the record's value-equality (which is over <c>_concurrency</c>, not the
    /// getter).</summary>
    public ConcurrencyOverride Concurrency
    {
        get => _concurrency ?? ConcurrencyOverride.Default;
        init => _concurrency = value == ConcurrencyOverride.Default ? null : value;
    }
}

/// <summary>Per-profile override for the dry-run evaluation worker count.
/// <see cref="ConcurrencyMode.Inherit"/> defers to the global setting;
/// <see cref="ConcurrencyMode.Automatic"/> lets the engine choose; <see cref="ConcurrencyMode.Manual"/>
/// uses <see cref="ManualWorkers"/>.</summary>
public sealed record ConcurrencyOverride
{
    /// <summary>The shared default (Inherit) used for profiles that never set a concurrency override.</summary>
    public static ConcurrencyOverride Default { get; } = new();

    public ConcurrencyMode Mode { get; init; } = ConcurrencyMode.Inherit;

    /// <summary>Worker count when <see cref="Mode"/> is Manual; clamped to >= 1 by the engine.
    /// Null / ignored for Inherit and Automatic.</summary>
    public int? ManualWorkers { get; init; }
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
    public required LogVerbosity Verbosity { get; init; }
    public required bool NotifyOnFailure { get; init; }
}

public enum SyncMode
{
    [Tooltip("Additive Archive")]
    AdditiveArchive,
    [Tooltip("Mirror")]
    Mirror,               /// [reserved — fails v1 validation]
}

public enum TargetLayout
{
    [Tooltip("Preserve Structure")]
    PreserveStructure,
    [Tooltip("Flatten")]
    Flatten,
}

public enum ConflictResolution
{
    [Tooltip("Overwrite")]
    Overwrite,
    [Tooltip("Overwrite If Newer")]
    OverwriteIfNewer,
    [Tooltip("Rename (Add Suffix)")]
    RenameSuffix,
    [Tooltip("Skip")]
    Skip,
}

public enum OverwriteHandling
{
    [Tooltip("Direct Overwrite")]
    DirectOverwrite,
    [Tooltip("Stage Overwrites")]
    StageOverwrites,
}

public enum VerificationMethod
{
    // Members are ordered by recommendation, strongest first: XxHash128 (the default) → SHA-256 →
    // SizeTimestamp (weak, reserved) → None (no verification, least recommended). XxHash128 (128-bit
    // XXH3) is fast, non-cryptographic, and collision-safe for integrity at realistic scale; it
    // occupies the zero slot so the enum default matches the app default. NOTE: journal records
    // serialize this enum as integers (JournalJsonContext has no UseStringEnumConverter), so this
    // ordering is a wire contract — reorder only pre-release; once WAL journals exist in the field,
    // append instead.
    [JsonStringEnumMemberName("XXH3-128")]
    [Tooltip("XxHash128 (XXH3)")]
    XxHash128,
    [JsonStringEnumMemberName("SHA256")]
    [Tooltip("SHA-256")]
    Sha256,
    [Tooltip("Size & Timestamp")]
    SizeTimestamp,        /// [reserved — fails v1 validation]
    [Tooltip("None")]
    None,
}

public enum OnSuccessAction
{
    [Tooltip("Keep Source")]
    KeepSource,
    [Tooltip("Move to Trash")]
    MoveToTrash,
    [Tooltip("Move to Archive")]
    MoveToArchive,
    [Tooltip("Permanently Delete")]
    PermanentDelete,
}

public enum OnFailureAction
{
    [Tooltip("Abort, Restore, & Clean")]
    AbortRestoreAndClean,   // single value; extension point (spec §5.1)
}

public enum MetadataOnConflict
{
    [Tooltip("Warn and Continue")]
    WarnAndContinue,
    [Tooltip("Fail Job")]
    FailJob,
}

public enum ArgumentMode
{
    [Tooltip("Literal")]
    Literal,
    [Tooltip("Shell")]
    Shell,                /// [reserved — fails v1 validation]
}

public enum OutputMode
{
    [Tooltip("New File")]
    NewFile,
    [Tooltip("In Place")]
    InPlace,
}

public enum MissedRunPolicy
{
    [Tooltip("Catch Up Once")]
    CatchUpOnce,
    [Tooltip("Skip")]
    Skip,
}

public enum LogVerbosity
{
    [Tooltip("Failures Only")]
    FailuresOnly,
    [Tooltip("Failures and Skips")]
    FailuresAndSkips,
    [Tooltip("All")]
    All,
}

/// <summary>How the dry-run evaluation worker count is chosen. The global setting uses only
/// Automatic/Manual; per-profile overrides additionally allow Inherit (defer to the global setting).</summary>
public enum ConcurrencyMode
{
    [Tooltip("Inherit")]
    Inherit,
    [Tooltip("Automatic")]
    Automatic,
    [Tooltip("Manual")]
    Manual,
}