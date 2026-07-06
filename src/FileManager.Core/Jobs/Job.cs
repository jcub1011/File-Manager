using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Primitives;
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
    public string Value { get; }

    public static Result<NormalizedPath, JobError> Create(string path) => throw new NotImplementedException();
    
    public int CompareTo(NormalizedPath other) => throw new NotImplementedException();

    public bool IsUnder(NormalizedPath ancestor) => throw new NotImplementedException();                  // containment checks for validation
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