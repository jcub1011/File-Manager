using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

public enum UnchangedCheckResult { NoExistingFile, ExistsDifferent, Unchanged }

public interface IAtomicPlacer
{
    /// <summary>Spec §3.4.1, BEFORE conflict resolution and before the output is sealed. Order:
    /// exists → size → (<see cref="IdentityPlan.AcceptMetadataMatch"/>: last-write time) →
    /// <see cref="IdentityPlan.ContentEvidence"/> (full hash of the existing target file, or its bounded
    /// sampled digest, against the matching digest on <paramref name="reference"/>).
    ///
    /// <para>Compares against <paramref name="reference"/> — the incoming file's identity, carrying a digest
    /// the caller computed once for all targets. It is deliberately NOT read from
    /// <c>JobExecution.Output</c>: this check runs before sealing precisely so that a duplicate never pays
    /// for sealing's full read.</para>
    ///
    /// <para>On Unchanged, journals target-unchanged and sets the target's
    /// <see cref="TargetState.SatisfiedUnchanged"/> state.</para></summary>
    Task<Result<UnchangedCheckResult, JobError>> CheckUnchangedAsync(
        JobExecution execution, int targetIndex, string finalPath, IdentityReference reference,
        CancellationToken ct = default);

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
