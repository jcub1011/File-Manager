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

    /// <summary>Destination paths a Mirror deletion pass journalled as about to be recycled but never
    /// journalled as finished, because the process died in between. Each is either still at its
    /// destination or already in the Recycle Bin — recovery cannot tell which and deliberately does
    /// nothing to them (see <c>CrashRecovery.ReportUnfinishedReconcilePasses</c>). Surfaced so startup
    /// can tell the user rather than leaving it in the service log; the next Mirror run re-plans and
    /// removes anything still there.
    /// <para>Optional/additive: defaults to empty so an existing construction site compiles unchanged.</para></summary>
    public IReadOnlyList<string> UnfinishedMirrorDeletions { get; init; } = [];
}
