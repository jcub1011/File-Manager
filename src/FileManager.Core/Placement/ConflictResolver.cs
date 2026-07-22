using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Locking;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace FileManager.Core.Placement;

/// <summary>Spec §3.4 conflict resolution. <see cref="Probe"/> is the read-only variant for
/// dry-run (I-DRYRUN-RO); <see cref="Resolve"/> is the live, lock-holding variant that consults
/// the session <see cref="SourcePriorityRegistry"/> for M:1 source-order priority and probes
/// RenameSuffix candidates through the <see cref="PathLockRegistry"/>.</summary>
public sealed class ConflictResolver(
    PathLockRegistry locks,
    SourcePriorityRegistry priorities,
    ILogger<ConflictResolver> logger) : IConflictResolver
{
    private const int SuffixBound = 10_000;

    public Result<ConflictOutcome, JobError> Resolve(
        string desiredFinalPath,
        ConflictResolution policy,
        SealedOutput output,
        int sourceIndex,
        Guid profileId,
        PathLockSet heldLocks)
    {
        try
        {
            switch (policy)
            {
                case ConflictResolution.Overwrite:
                    return PriorityKeepsExisting(profileId, desiredFinalPath, sourceIndex)
                        ? new ConflictOutcome(ConflictAction.SkipExistingKept, desiredFinalPath)
                        : new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

                case ConflictResolution.OverwriteIfNewer:
                    if (PriorityKeepsExisting(profileId, desiredFinalPath, sourceIndex))
                        return new ConflictOutcome(ConflictAction.SkipExistingKept, desiredFinalPath);
                    if (!File.Exists(desiredFinalPath))
                        return new ConflictOutcome(ConflictAction.Write, desiredFinalPath);
                    return output.SourceLastWriteUtc > File.GetLastWriteTimeUtc(desiredFinalPath)
                        ? new ConflictOutcome(ConflictAction.Write, desiredFinalPath)
                        : new ConflictOutcome(ConflictAction.SkipExistingKept, desiredFinalPath);

                case ConflictResolution.Skip:
                    return File.Exists(desiredFinalPath)
                        ? new ConflictOutcome(ConflictAction.SkipExistingKept, desiredFinalPath)
                        : new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

                case ConflictResolution.RenameSuffix:
                    return ResolveRenameSuffix(desiredFinalPath, heldLocks);

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
                Message = $"could not resolve \"{desiredFinalPath}\": {ex.Message}",
                Path = desiredFinalPath,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Conflict resolution for {Path} failed unexpectedly", desiredFinalPath);
            return new JobError
            {
                Code = JobErrorCode.ConflictUnresolvable,
                Message = $"could not resolve \"{desiredFinalPath}\": {ex.GetType().Name}: {ex.Message}",
                Path = desiredFinalPath,
            };
        }
    }

    /// <summary>M:1 priority (spec §3.4): under Overwrite/OverwriteIfNewer, an incoming file from a
    /// higher (lower-priority) source index that collides with a final path a lower index already
    /// placed this session keeps the existing file.</summary>
    private bool PriorityKeepsExisting(Guid profileId, string desiredFinalPath, int sourceIndex)
    {
        Result<NormalizedPath, JobError> normalized = NormalizedPath.Create(desiredFinalPath);
        if (!normalized.TryGetValue(out NormalizedPath key))
            return false;
        if (priorities.TryGetPriority(profileId, key, out int placedByIndex) && sourceIndex > placedByIndex)
        {
            logger.LogInformation(
                "Keeping existing \"{Path}\": placed this session by higher-priority source {Placed} (incoming source {Incoming})",
                desiredFinalPath, placedByIndex, sourceIndex);
            return true;
        }
        return false;
    }

    private Result<ConflictOutcome, JobError> ResolveRenameSuffix(string desiredFinalPath, PathLockSet heldLocks)
    {
        // A fresh desired name: take it if free and lockable — otherwise fall through to suffixing.
        if (!File.Exists(desiredFinalPath) && TryReserve(desiredFinalPath, heldLocks))
            return new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

        string directory = Path.GetDirectoryName(desiredFinalPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(desiredFinalPath);
        string extension = Path.GetExtension(desiredFinalPath);

        for (int i = 1; i <= SuffixBound; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            // Skip a candidate that exists, or that another job holds the lock for (it is about to
            // create it) — TryAcquireAdditional never blocks while we hold our set (I-LOCK-ORDER).
            if (!File.Exists(candidate) && TryReserve(candidate, heldLocks))
                return new ConflictOutcome(ConflictAction.Write, candidate);
        }

        logger.LogWarning("RenameSuffix resolution exhausted {Bound} candidates for {Path}", SuffixBound, desiredFinalPath);
        return new JobError
        {
            Code = JobErrorCode.ConflictUnresolvable,
            Message = $"no free rename-suffix name within {SuffixBound} candidates for \"{desiredFinalPath}\"",
            Path = desiredFinalPath,
        };
    }

    private bool TryReserve(string candidate, PathLockSet heldLocks)
    {
        Result<NormalizedPath, JobError> normalized = NormalizedPath.Create(candidate);
        return normalized.TryGetValue(out NormalizedPath path) && locks.TryAcquireAdditional(heldLocks, path);
    }

    public Result<ConflictOutcome, JobError> Probe(
        string desiredFinalPath, ConflictResolution policy, DateTimeOffset incomingLastWriteUtc,
        bool desiredFinalExists, DateTimeOffset existingLastWriteUtc)
    {
        try
        {
            if (!desiredFinalExists)
                return new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

            switch (policy)
            {
                case ConflictResolution.Overwrite:
                    return new ConflictOutcome(ConflictAction.Write, desiredFinalPath);

                case ConflictResolution.OverwriteIfNewer:
                    return incomingLastWriteUtc > existingLastWriteUtc
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
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Conflict probe for {Path} failed unexpectedly", desiredFinalPath);
            return new JobError
            {
                Code = JobErrorCode.ConflictUnresolvable,
                Message = $"could not probe \"{desiredFinalPath}\": {ex.GetType().Name}: {ex.Message}",
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
