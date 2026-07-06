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
    public required string Sha256 { get; init; }       // "" for VerificationMethod.None
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
    UnplacedAndRestored, UnplacedNoPrior, LeftInPlaceUnrecoverable
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

[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JournalRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
