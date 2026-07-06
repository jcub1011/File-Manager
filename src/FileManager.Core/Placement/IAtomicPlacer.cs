using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

public enum UnchangedCheckResult { NoExistingFile, ExistsDifferent, Unchanged }

public interface IAtomicPlacer
{
    /// <summary>Spec §3.4.1, BEFORE conflict resolution. Order: exists → size → (SHA256: stream
    /// hash of the existing target file vs the sealed output | None: best-effort mtime).
    /// Compares against the SEALED OUTPUT (post-transform), never the raw source.
    /// On Unchanged, journals target-unchanged. [seam: SizeTimestamp adds a branch here]</summary>
    Task<Result<UnchangedCheckResult, JobError>> CheckUnchangedAsync(
        JobExecution execution, int targetIndex, string finalPath, CancellationToken ct = default);

    /// <summary>Journal twb → copy to temp (hash-on-write) → Flush(true) → read-back verify →
    /// journal tver → apply metadata → [journal tstg → stage] → atomic rename → journal tplc.</summary>
    Task<Result<PlacementResult, JobError>> PlaceTargetAsync(
        PlacementRequest request, CancellationToken ct = default);
}

public sealed record PlacementRequest
{
    public required JobExecution Execution { get; init; }
    public required int TargetIndex { get; init; }
    public required SealedOutput Output { get; init; }
    public required string FinalPath { get; init; }        // post-conflict-resolution, lock held
    public required bool FinalExists { get; init; }
    public required OverwriteHandling OverwriteHandling { get; init; }
    public required VerificationMethod Verification { get; init; }
}

public sealed record PlacementResult
{
    public required TargetState FinalState { get; init; }  // Placed
    public required string? StagedPath { get; init; }
}
