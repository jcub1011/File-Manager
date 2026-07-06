using FileManager.Core.Jobs;
using System;

namespace FileManager.Core.Placement;

/// <summary>
/// M:1 source-order priority (spec §3.4). Interpreted shape — the doc describes this type's
/// behavior in prose only (§4.6), with no code block: a session-scoped, in-memory map of
/// (ProfileId, NormalizedPath finalPath) → sourceIndex, written by <see cref="IAtomicPlacer"/>
/// whenever a target is Placed/SatisfiedUnchanged, read by <see cref="IConflictResolver.Resolve"/>.
/// </summary>
public sealed class SourcePriorityRegistry
{
    public void RecordPlacement(Guid profileId, NormalizedPath finalPath, int sourceIndex) =>
        throw new NotImplementedException();

    public bool TryGetPriority(Guid profileId, NormalizedPath finalPath, out int sourceIndex) =>
        throw new NotImplementedException();
}
