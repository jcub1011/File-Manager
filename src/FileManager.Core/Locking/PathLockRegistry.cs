using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Locking;

public sealed class PathLockRegistry
{
    /// <summary>
    /// Acquires every path or waits FIFO. Deadlock-free: paths are sorted by NormalizedPath's
    /// global ordinal ordering and acquired strictly in that order by every caller, so no wait
    /// cycle can form (I-LOCK-ORDER).
    /// </summary>
    public ValueTask<PathLockSet> AcquireAsync(
        IReadOnlyCollection<NormalizedPath> paths, JobId owner, CancellationToken ct = default) =>
        throw new NotImplementedException();

    /// <summary>
    /// Non-blocking acquire of one extra path while already holding a set — used only for
    /// RenameSuffix candidate probing (§4.6). Never waits, so it cannot create a wait cycle.
    /// </summary>
    public bool TryAcquireAdditional(PathLockSet held, NormalizedPath path) =>
        throw new NotImplementedException();
}

/// <summary>Releases all held paths (reverse order) on dispose. A Job holds exactly one set for its lifetime.</summary>
public sealed class PathLockSet : IAsyncDisposable
{
    public IReadOnlyList<NormalizedPath> Paths { get; }

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
