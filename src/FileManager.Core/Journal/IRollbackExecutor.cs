using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using System.Collections.Generic;
using System.Threading;

namespace FileManager.Core.Journal;

public interface IRollbackExecutor
{
    Result<RollbackResult, JobError> Rollback(RollbackContext context, CancellationToken ct = default);
}

public sealed record RollbackContext
{
    public required JobId JobId { get; init; }
    public required JobError Cause { get; init; }
    /// <summary>Built from live state, or reconstructed from the journal by recovery.</summary>
    public required IReadOnlyList<TargetRollbackItem> Targets { get; init; }
    public required string WorkspaceDir { get; init; }
    public required OverwriteHandling OverwriteHandling { get; init; }
}

public sealed record TargetRollbackItem
{
    public required int TargetIndex { get; init; }
    public required TargetState State { get; init; }
    public required string? TempPath { get; init; }
    public required string? FinalPath { get; init; }
    public required string? StagedPath { get; init; }
    public required bool FinalExistedBeforeJob { get; init; }
}

public sealed record RollbackResult
{
    public required bool Complete { get; init; }
    public required IReadOnlyList<string> ResidualPaths { get; init; }
}
