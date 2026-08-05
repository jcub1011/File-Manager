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

    /// <summary>When a Mirror run removes the destination files that no longer exist in the source.
    /// Only meaningful under <see cref="SyncMode.Mirror"/>. Optional/additive: absent in legacy JSON
    /// deserializes to the zero slot, <see cref="MirrorDeletion.AfterCopy"/>.</summary>
    public MirrorDeletion MirrorDeletion { get; init; }

    /// <summary>What evidence proves "the destination already holds this content" for files LARGER than
    /// <see cref="LargeFileIdentityThresholdBytes"/> — the spec §3.4.1 unchanged-file short-circuit only.
    /// This is an IDENTITY axis, deliberately separate from <see cref="VerificationMethod"/>: the
    /// post-copy read-back verification of every file actually written stays a full content hash under
    /// <see cref="VerificationMethod"/> whatever this is set to, so the guarantee that authorizes source
    /// disposition is untouched. Optional/additive: absent in legacy JSON deserializes to the zero slot,
    /// <see cref="LargeFileIdentity.FullHash"/>, which is the pre-existing behaviour.</summary>
    public LargeFileIdentity LargeFileIdentity { get; init; }

    private readonly long? _largeFileIdentityThresholdBytes;

    /// <summary>Files at or below this size always use a full content hash for the unchanged-check —
    /// exact, and cheap enough at this scale that there is nothing to gain. Only files ABOVE it use
    /// <see cref="LargeFileIdentity"/>.
    ///
    /// <para>Nullable backing field because the default is non-zero and the source generator does not run
    /// property initializers for absent members: a plain <c>long</c> would read back as 0 from a legacy
    /// profile, which means "every file is large" — the dangerous direction. The setter collapses the
    /// default to the absent representation so an omitted field and an explicit
    /// <see cref="DefaultLargeFileIdentityThresholdBytes"/> compare equal under the record's
    /// value-equality.</para></summary>
    [JsonIgnore]
    public long LargeFileIdentityThresholdBytes
    {
        get => _largeFileIdentityThresholdBytes ?? DefaultLargeFileIdentityThresholdBytes;
        init => _largeFileIdentityThresholdBytes = value == DefaultLargeFileIdentityThresholdBytes ? null : value;
    }

    /// <summary>Serialization surface for <see cref="LargeFileIdentityThresholdBytes"/>: carries the
    /// nullable backing representation so the default stays ABSENT in the profile JSON and on the wire.</summary>
    [JsonInclude, JsonPropertyName("LargeFileIdentityThresholdBytes")]
    public long? LargeFileIdentityThresholdBytesSerialized
    {
        get => _largeFileIdentityThresholdBytes;
        init => _largeFileIdentityThresholdBytes = value == DefaultLargeFileIdentityThresholdBytes ? null : value;
    }

    /// <summary>256 MiB. Large enough that ordinary documents, photos and source files keep the exact
    /// full-hash check (hashing them costs milliseconds), small enough that the media, disk-image and
    /// archive files whose full read actually hurts fall on the cheap side.</summary>
    public const long DefaultLargeFileIdentityThresholdBytes = 256L * 1024 * 1024;
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
    Mirror,
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

public enum MirrorDeletion
{
    // AfterCopy occupies the zero slot so the enum default matches the app default: a legacy profile
    // with no MirrorDeletion member deserializes to the safe choice (the source-generated
    // deserializer does not run property initializers for absent members).
    [Tooltip("Delete After Copy", "Copy and verify everything first, then remove the destination files. Safer, but the drive must hold both the incoming data and the files awaiting deletion at the same time.")]
    AfterCopy,
    [Tooltip("Delete Proactively", "Remove the destination files first, then copy. Needs far less free space, but the old copy is gone before its replacement exists.")]
    Proactive,
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

/// <summary>How the spec §3.4.1 unchanged-file short-circuit decides that a destination already holds
/// the incoming content, for files above <see cref="PolicySettings.LargeFileIdentityThresholdBytes"/>.
///
/// <para>This exists because proving identity by full content hash costs a complete read of BOTH files,
/// which dominates everything else on large media. Note the asymmetry that makes the cheap members
/// defensible at all: a partial read is EXACT when it says "these files differ" (sampled bytes that
/// differ prove the files differ) and only probabilistic when it says "identical". A false "identical"
/// skips a copy, so pairing a cheap member with an <see cref="OnSuccessAction"/> that removes the source
/// is a data-loss path — <c>IProfileValidator</c> raises a blocking warning for that combination.</para>
///
/// <para>FullHash occupies the zero slot so the enum default matches the app default and a legacy profile
/// with no member deserializes to the pre-existing exact behaviour (the source-generated deserializer
/// does not run property initializers for absent members). Members are ordered strongest first. Append
/// only — this enum reaches <c>PolicySnapshot</c>, and profile enums serialize as INTEGERS in journal
/// records.</para></summary>
public enum LargeFileIdentity
{
    [Tooltip("Full content hash", "Read both files end to end and compare their content hashes. Exact, and the slowest — a duplicate 8 GiB video costs about 16 GiB of reads.")]
    FullHash,
    [Tooltip("Sampled content hash", "Hash the file's length plus a fixed 8 MiB of it — the start, the end, and six evenly spaced blocks in between — however large the file is. Catches truncation and re-encodes reliably, but an edit confined to a region it does not sample would be missed.")]
    SampledHash,
    [Tooltip("Timestamp, else sampled hash", "Treat files with the same size and modified time as identical without reading anything. If the timestamps differ, fall back to the sampled content hash rather than copying blindly.")]
    TimestampOrSampledHash,
    [Tooltip("Size & modified time", "Compare size and modified time only — no file content is read at all. The fastest option and the weakest: any tool that restores the modified time after editing a file defeats it.")]
    SizeAndTimestamp,
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