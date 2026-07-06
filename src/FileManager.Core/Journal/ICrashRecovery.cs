using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System.Collections.Generic;
using System.Threading;

namespace FileManager.Core.Journal;

public interface ICrashRecovery
{
    Result<RecoveryReport, JobError> Recover(CancellationToken ct = default);
}

public sealed record RecoveryReport
{
    public required int JobsRecovered { get; init; }
    public required int CompletedForward { get; init; }
    public required int RolledBack { get; init; }
    public required int CleanedPrePlacement { get; init; }
    public required IReadOnlyList<string> QuarantinedPaths { get; init; }
}
