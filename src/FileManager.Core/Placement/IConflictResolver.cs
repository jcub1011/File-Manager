using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Locking;
using System;

namespace FileManager.Core.Placement;

public enum ConflictAction { Write, SkipExistingKept }

public sealed record ConflictOutcome(ConflictAction Action, string FinalPath);

public interface IConflictResolver
{
    Result<ConflictOutcome, JobError> Resolve(
        string desiredFinalPath,
        ConflictResolution policy,
        SealedOutput output,          // OverwriteIfNewer compares SourceLastWriteUtc
        int sourceIndex,              // M:1 priority (below)
        Guid profileId,
        PathLockSet heldLocks);

    /// <summary>Read-only variant for dry-run (I-DRYRUN-RO): computes the outcome that Resolve
    /// would choose, without acquiring locks or consulting the priority registry's write side.</summary>
    Result<ConflictOutcome, JobError> Probe(
        string desiredFinalPath, ConflictResolution policy, DateTimeOffset incomingLastWriteUtc);
}
