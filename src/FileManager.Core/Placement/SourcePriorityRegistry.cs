using FileManager.Core.Jobs;
using System;
using System.Collections.Concurrent;

namespace FileManager.Core.Placement;

/// <summary>
/// M:1 source-order priority (spec §3.4). Interpreted shape — the doc describes this type's
/// behavior in prose only (§4.6), with no code block: a session-scoped, in-memory map of
/// (ProfileId, NormalizedPath finalPath) → sourceIndex, written by <see cref="IAtomicPlacer"/>
/// whenever a target is Placed/SatisfiedUnchanged, read by <see cref="IConflictResolver.Resolve"/>.
/// Provenance is deliberately not persisted in v1 — across a restart priority degrades to arrival
/// order (a documented limitation, §4.6).
/// </summary>
public sealed class SourcePriorityRegistry
{
    private readonly ConcurrentDictionary<(Guid ProfileId, NormalizedPath FinalPath), int> _placements = new();

    /// <summary>Records that <paramref name="finalPath"/> was placed/satisfied this session by the
    /// source at <paramref name="sourceIndex"/>. Keeps the lowest (highest-priority) index seen.</summary>
    public void RecordPlacement(Guid profileId, NormalizedPath finalPath, int sourceIndex) =>
        _placements.AddOrUpdate(
            (profileId, finalPath), sourceIndex,
            (_, existing) => Math.Min(existing, sourceIndex));

    public bool TryGetPriority(Guid profileId, NormalizedPath finalPath, out int sourceIndex) =>
        _placements.TryGetValue((profileId, finalPath), out sourceIndex);
}
