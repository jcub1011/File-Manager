using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Locking;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace FileManager.Core.Placement;

/// <summary>Spec §3.4 conflict resolution. This slice ships only the read-only
/// <see cref="Probe"/> used by dry-run (I-DRYRUN-RO); <see cref="Resolve"/> — the live,
/// lock-holding variant with SourcePriorityRegistry — lands with the job executor.</summary>
public sealed class ConflictResolver(ILogger<ConflictResolver> logger) : IConflictResolver
{
    private const int SuffixBound = 10_000;

    public Result<ConflictOutcome, JobError> Resolve(
        string desiredFinalPath,
        ConflictResolution policy,
        SealedOutput output,
        int sourceIndex,
        Guid profileId,
        PathLockSet heldLocks) =>
        throw new NotSupportedException(
            "Job execution is not part of this slice — only dry-run's Probe is implemented (architecture-v1.md §4.6).");

    public Result<ConflictOutcome, JobError> Probe(
        string desiredFinalPath, ConflictResolution policy, DateTimeOffset incomingLastWriteUtc)
    {
        try
        {
            if (!File.Exists(desiredFinalPath))
                return new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

            switch (policy)
            {
                case ConflictResolution.Overwrite:
                    return new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

                case ConflictResolution.OverwriteIfNewer:
                    DateTimeOffset existingLastWrite = File.GetLastWriteTimeUtc(desiredFinalPath);
                    return incomingLastWriteUtc > existingLastWrite
                        ? new ConflictOutcome(ConflictAction.Write, desiredFinalPath)
                        : new ConflictOutcome(ConflictAction.SkipExistingKept, desiredFinalPath);

                case ConflictResolution.Skip:
                    return new ConflictOutcome(ConflictAction.SkipExistingKept, desiredFinalPath);

                case ConflictResolution.RenameSuffix:
                    return ProbeRenameSuffix(desiredFinalPath);

                default:
                    return new JobError
                    {
                        Code = JobErrorCode.ConflictUnresolvable,
                        Message = $"unknown ConflictResolution {policy}",
                        Path = desiredFinalPath,
                    };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JobError
            {
                Code = JobErrorCode.ConflictUnresolvable,
                Message = $"could not probe \"{desiredFinalPath}\": {ex.Message}",
                Path = desiredFinalPath,
            };
        }
    }

    private Result<ConflictOutcome, JobError> ProbeRenameSuffix(string desiredFinalPath)
    {
        string directory = Path.GetDirectoryName(desiredFinalPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(desiredFinalPath);
        string extension = Path.GetExtension(desiredFinalPath);

        for (int i = 1; i <= SuffixBound; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return new ConflictOutcome(ConflictAction.Write, candidate);
        }

        logger.LogWarning("RenameSuffix probing exhausted {Bound} candidates for {Path}", SuffixBound, desiredFinalPath);
        return new JobError
        {
            Code = JobErrorCode.ConflictUnresolvable,
            Message = $"no free rename-suffix name within {SuffixBound} candidates for \"{desiredFinalPath}\"",
            Path = desiredFinalPath,
        };
    }
}
