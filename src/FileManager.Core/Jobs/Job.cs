using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Jobs;

public enum JobErrorCode
{
    SelfPathTarget, InsufficientDiskSpace, SourceDisposed, SourceUnreadable,
    TransformerFailed, TransformerTimeout, TargetWriteFailed, VerificationMismatch,
    StagingFailed, PlacementFailed, DispositionFailed, RollbackIncomplete,
    JournalWriteFailed, LockTimeout, MetadataConflict, ConflictUnresolvable
}

public sealed record JobError
{
    public required JobErrorCode Code { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
    public int? TargetIndex { get; init; }
}

public readonly record struct JobId(Guid Value)
{
    public static JobId New() => new(Guid.CreateVersion7());       // time-ordered — sorts by creation
    public string Short => Value.ToString("N")[..8];               // temp-file suffix (§4.6)
}

/// <summary>Absolute, Path.GetFullPath-canonicalized, trailing-separator-trimmed.
/// Equality/hash: OrdinalIgnoreCase on Windows. Comparison defines the global lock order (I-LOCK-ORDER).</summary>
public readonly record struct NormalizedPath : IComparable<NormalizedPath>
{
    // Filesystem case-sensitivity is a per-OS fact, not an OS code path — this is the one
    // sanctioned OperatingSystem probe in Core (§1 rule 2 note).
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private NormalizedPath(string value) => Value = value;

    public string Value { get; }

    public static Result<NormalizedPath, JobError> Create(string path)
    {
        // JobErrorCode has no dedicated malformed-path member (§5.4 is spec-frozen);
        // SourceUnreadable is the generic "this path is unusable" bucket — callers key off
        // the message, and the validator re-wraps failures as PROFILE_PATH_INVALID.
        if (string.IsNullOrWhiteSpace(path))
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = "path is empty", Path = path };
        if (!System.IO.Path.IsPathFullyQualified(path))
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = $"path is not absolute: \"{path}\"", Path = path };
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            return new NormalizedPath(System.IO.Path.TrimEndingDirectorySeparator(full));
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException or NotSupportedException)
        {
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = $"malformed path \"{path}\": {ex.Message}", Path = path };
        }
    }

    public bool Equals(NormalizedPath other) => string.Equals(Value, other.Value, Comparison);

    public override int GetHashCode() =>
        Value is null ? 0 : string.GetHashCode(Value, Comparison);

    public int CompareTo(NormalizedPath other) =>
        string.Compare(Value, other.Value, Comparison);

    /// <summary>Strict containment: true when this path is inside <paramref name="ancestor"/>
    /// (never for equal paths). Boundary-safe: "C:\ab" is not under "C:\a".</summary>
    public bool IsUnder(NormalizedPath ancestor)
    {
        if (Value is null || ancestor.Value is null)
            return false;
        if (Value.Length <= ancestor.Value.Length)
            return false;
        if (!Value.StartsWith(ancestor.Value, Comparison))
            return false;
        // Ancestor may itself end in a separator only when it is a volume root ("C:\").
        char boundary = Value[ancestor.Value.Length];
        return ancestor.Value[^1] == System.IO.Path.DirectorySeparatorChar
            || ancestor.Value[^1] == System.IO.Path.AltDirectorySeparatorChar
            || boundary == System.IO.Path.DirectorySeparatorChar
            || boundary == System.IO.Path.AltDirectorySeparatorChar;
    }

    public override string ToString() => Value ?? string.Empty;
}

public enum TriggerKind { Watcher, Schedule, CatchUp, ManualShell, Cli }

public sealed record Payload(
    Guid ProfileId, string SourcePath, string SourceRoot, TriggerKind Trigger, DateTimeOffset EnqueuedAt);

/// <summary>Immutable snapshot at journal-open; recovery compares the filesystem against it.</summary>
public sealed record SourceSnapshot
{
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
}

public sealed record TargetPlan
{
    public required int TargetIndex { get; init; }
    public required string TargetRoot { get; init; }
    /// <summary>TargetLayout-resolved destination, before conflict-resolution suffixing.</summary>
    public required string ProspectiveFinalPath { get; init; }
}

/// <summary>The policy fields the journal snapshots so recovery never depends on a profile edit.</summary>
public sealed record PolicySnapshot
{
    public required VerificationMethod Verification { get; init; }
    public required OverwriteHandling OverwriteHandling { get; init; }
    public required ConflictResolution ConflictResolution { get; init; }
    public required OnSuccessAction OnSuccess { get; init; }
    public string? ArchiveFolder { get; init; }
    public required MetadataOnConflict MetadataOnConflict { get; init; }
}

public sealed record JobPlan
{
    public required JobId JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required Profile Profile { get; init; }                 // full snapshot for the executor
    public required Payload Payload { get; init; }
    public required SourceSnapshot Source { get; init; }
    public required FileMetadata SourceMetadata { get; init; }     // existing Core.Files type
    public required IReadOnlyList<TargetPlan> Targets { get; init; }
    public required PolicySnapshot Policies { get; init; }
    public required string WorkspaceDir { get; init; }             // <tempRoot>/.pipeline_tmp/<JobId>/
}

/// <summary>Sealed transform output — the reference every verification compares against (§7.1 state OutputSealed).</summary>
public sealed record SealedOutput
{
    public required string Path { get; init; }         // workspace artifact; == source path when no transformers
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }       // "" when VerificationMethod.None
    public required DateTimeOffset SourceLastWriteUtc { get; init; }
}

/// <summary>Mutable per-job execution state, owned by one pool worker; wraps the plan,
/// the state machine, the lock set, suppression tokens, and per-target progress.</summary>
public sealed class JobExecution
{
    public required JobPlan Plan { get; init; }
    public JobStateMachine States { get; }
    public SealedOutput? Output { get; set; }
    public IReadOnlyList<TargetProgress> Targets { get; }
}

public sealed class TargetProgress
{
    public required TargetPlan Plan { get; init; }
    public TargetState State { get; set; }
    public string? TempPath { get; set; }
    public string? FinalPath { get; set; }             // post-conflict-resolution
    public string? StagedPath { get; set; }
    public bool FinalExistedBeforeJob { get; set; }
}

public enum JobState
{
    Ingested, Locked, Opened, Preflighted, Screened, Transforming,
    OutputSealed, Distributing, Committed, Disposing, RollingBack, Closed
}

public enum TargetState
{
    Pending, SatisfiedUnchanged, SkippedConflict, TempWriting, TempWritten,
    Verified, Staged, Placed, RolledBack, RollbackFailed
}

public enum JobOutcome { Succeeded, Skipped, Failed, RollbackFailed }

public enum SkipReason { Filtered, SourceDisposed, UnchangedAtAllTargets }

public sealed record JobCompletion(
    JobId JobId, JobOutcome Outcome, SkipReason? SkipReason, JobError? Error, TimeSpan Duration);