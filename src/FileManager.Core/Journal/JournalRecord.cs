using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Core.Journal;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(JobOpenedRecord), "open")]
[JsonDerivedType(typeof(OutputSealedRecord), "sealed")]
[JsonDerivedType(typeof(TargetWriteBeginRecord), "twb")]
[JsonDerivedType(typeof(TargetVerifiedRecord), "tver")]
[JsonDerivedType(typeof(TargetStagedRecord), "tstg")]
[JsonDerivedType(typeof(TargetPlacedRecord), "tplc")]
[JsonDerivedType(typeof(TargetUnchangedRecord), "tunc")]
[JsonDerivedType(typeof(TargetSkippedRecord), "tskp")]
[JsonDerivedType(typeof(JobCommittedRecord), "commit")]
[JsonDerivedType(typeof(RollbackBeginRecord), "rbbegin")]
[JsonDerivedType(typeof(TargetRolledBackRecord), "trb")]
[JsonDerivedType(typeof(JobClosedRecord), "close")]
// Mirror reconcile pass (§10.1). Appended, never reordered — the discriminator list is a wire
// contract, and an existing journal on disk must keep deserializing.
[JsonDerivedType(typeof(MirrorReconcileOpenedRecord), "mropen")]
[JsonDerivedType(typeof(MirrorOrphanTrashingRecord), "mrdel")]
[JsonDerivedType(typeof(MirrorOrphanTrashedRecord), "mrdone")]
[JsonDerivedType(typeof(MirrorReconcileClosedRecord), "mrclose")]
public abstract record JournalRecord
{
    public required Guid JobId { get; init; }
    public required long Seq { get; init; }            // monotonic per journal writer
    public required DateTimeOffset AtUtc { get; init; }
}

public sealed record JobOpenedRecord : JournalRecord
{
    public required Guid ProfileId { get; init; }
    public required SourceSnapshot Source { get; init; }
    public required PolicySnapshot Policies { get; init; }
    public required string WorkspaceDir { get; init; }
    public required IReadOnlyList<TargetPlan> Targets { get; init; }
}

public sealed record OutputSealedRecord : JournalRecord
{
    public required string OutputPath { get; init; }
    public required long SizeBytes { get; init; }
    // Hashed under the job's PolicySnapshot.Verification (JobOpenedRecord); "" for
    // VerificationMethod.None. Crash recovery re-hashes with that same method.
    public required string ContentHash { get; init; }
}

public sealed record TargetWriteBeginRecord : JournalRecord
{
    public required int TargetIndex { get; init; }
    public required string TempPath { get; init; }
    public required string FinalPath { get; init; }    // post-conflict-resolution
    public required bool FinalExisted { get; init; }   // drives staging + rollback decisions
}

public sealed record TargetVerifiedRecord : JournalRecord
{
    public required int TargetIndex { get; init; }
}

public sealed record TargetStagedRecord : JournalRecord
{
    public required int TargetIndex { get; init; }
    public required string FinalPath { get; init; }
    public required string StagedPath { get; init; }   // <TargetRoot>\.fm_staging\<JobId>\<finalName>
}

public sealed record TargetPlacedRecord : JournalRecord
{
    public required int TargetIndex { get; init; }
}

public sealed record TargetUnchangedRecord : JournalRecord
{
    public required int TargetIndex { get; init; }
    public required string FinalPath { get; init; }
}

public sealed record TargetSkippedRecord : JournalRecord   // ConflictResolution.Skip kept the existing file
{
    public required int TargetIndex { get; init; }
}

public sealed record JobCommittedRecord : JournalRecord;   // THE commit point (I-DISPOSE)

public sealed record RollbackBeginRecord : JournalRecord
{
    public required string Reason { get; init; }
    public int? FailedTargetIndex { get; init; }
}

public enum RollbackAction
{
    None, RemovedTemp, RestoredStagedBeforePlacement,
    UnplacedAndRestored, UnplacedNoPrior, LeftInPlaceUnrecoverable,
    // Appended (journal wire contract — records serialize enums as integers; never reorder).
    /// <summary>The placed final no longer matched the job's own output hash: someone modified it
    /// after placement, so rollback left it (and any staged prior) untouched.</summary>
    LeftInPlaceModified,
}

public sealed record TargetRolledBackRecord : JournalRecord
{
    public required int TargetIndex { get; init; }
    public required RollbackAction Action { get; init; }
    public string? Error { get; init; }                // non-null => this target's rollback failed
}

public sealed record JobClosedRecord : JournalRecord
{
    public required JobOutcome Outcome { get; init; }
    public SkipReason? SkipReason { get; init; }
    public string? DispositionError { get; init; }     // Phase-6 failure: logged, never rolled back
}

// ---- Mirror reconcile pass (§10.1) --------------------------------------------------------------

/// <summary>Base for the records a Mirror reconcile pass writes.
///
/// <para><b>Its <see cref="JournalRecord.JobId"/> is a PASS id, not a copy job's.</b> Reusing the
/// field (and <see cref="Jobs.JobId"/> itself) is what lets a pass be narrated through the existing
/// per-job log and drilled into with <c>get-job-log</c> for free. The consequence is that crash
/// recovery must never treat such a group as a job to resolve: it has no <c>job-opened</c>, no
/// targets, and nothing recovery could correctly do to it. See
/// <c>CrashRecovery.ReportUnfinishedReconcilePasses</c>.</para></summary>
public abstract record MirrorReconcileRecord : JournalRecord;

public sealed record MirrorReconcileOpenedRecord : MirrorReconcileRecord
{
    public required Guid ProfileId { get; init; }
    public required Guid RunId { get; init; }
    public required MirrorDeletion Timing { get; init; }
    public required IReadOnlyList<string> TargetRoots { get; init; }
    public required int OrphanCount { get; init; }
    public required long OrphanBytes { get; init; }
}

/// <summary>WRITE-AHEAD (§7.2): appended and fsync'd BEFORE the trash move, so no destination file is
/// ever removed without a durable record naming it. A <c>mrdel</c> with no matching
/// <see cref="MirrorOrphanTrashedRecord"/> is the one ambiguous state a crash can leave — and it is
/// ambiguous safely: the file is either still on disk (the next run removes it again; the pass is
/// idempotent) or already in the Recycle Bin, where the user can retrieve it.</summary>
public sealed record MirrorOrphanTrashingRecord : MirrorReconcileRecord
{
    public required string Path { get; init; }
    public required string TargetRoot { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record MirrorOrphanTrashedRecord : MirrorReconcileRecord
{
    public required string Path { get; init; }
    /// <summary>Non-null means this orphan was NOT removed — the trash call or its audit row failed.</summary>
    public string? Error { get; init; }
}

public sealed record MirrorReconcileClosedRecord : MirrorReconcileRecord
{
    public required MirrorReconcileOutcome Outcome { get; init; }
    public required int Deleted { get; init; }
    public required int Skipped { get; init; }
    public required long BytesDeleted { get; init; }
    /// <summary>Why nothing (or nothing further) was deleted. Non-null on either aborted outcome.</summary>
    public string? AbortReason { get; init; }
}

/// <summary>How a reconcile pass ended.
/// <para>Journal wire contract — records serialize enums as integers (this context deliberately has no
/// string-enum converter), so members may be APPENDED but never reordered.</para></summary>
public enum MirrorReconcileOutcome
{
    /// <summary>Every orphan the plan named was removed.</summary>
    Completed,
    /// <summary>The pass ran, but at least one orphan was skipped or failed to be removed.</summary>
    PartiallyCompleted,
    /// <summary>A safety gate refused the pass before anything was deleted.</summary>
    AbortedBeforeDeleting,
    /// <summary>The pass began deleting and then stopped (shutdown, pause, repeated journal failure).
    /// Whatever reached the Recycle Bin stays there — it is recoverable, and re-deleting or restoring
    /// it would both be wrong.</summary>
    AbortedMidPass,
    /// <summary>The plan named no orphans.</summary>
    NothingToDo,
}

[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
