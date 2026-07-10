using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Locking;

/// <summary>In-process async path locks (§4.3). Authoritative for this engine instance only —
/// not a cross-machine mutex (spec §5.4 network caveat). Deadlock-free by I-LOCK-ORDER: every
/// caller acquires a set strictly in <see cref="NormalizedPath"/> ordinal order, and the only
/// acquire-while-holding path (<see cref="TryAcquireAdditional"/>) never waits.</summary>
public sealed class PathLockRegistry
{
    private sealed record Waiter(JobId Owner, TaskCompletionSource Tcs);

    private sealed class LockEntry
    {
        public JobId Holder;
        public readonly Queue<Waiter> Waiters = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<NormalizedPath, LockEntry> _entries = new();

    /// <summary>Acquires every path or waits FIFO. Paths are sorted by ordinal ordering and
    /// acquired in that order by every caller, so no wait cycle can form (I-LOCK-ORDER).</summary>
    public async ValueTask<PathLockSet> AcquireAsync(
        IReadOnlyCollection<NormalizedPath> paths, JobId owner, CancellationToken ct = default)
    {
        // Distinct + globally ordered — the deadlock-freedom precondition.
        NormalizedPath[] ordered = paths.Distinct().OrderBy(p => p).ToArray();
        var set = new PathLockSet(this, owner);
        try
        {
            foreach (NormalizedPath path in ordered)
            {
                await AcquireOneAsync(path, owner, ct).ConfigureAwait(false);
                set.AddAcquired(path);
            }
            return set;
        }
        catch
        {
            // Release whatever we already hold before surfacing the cancellation/failure —
            // never leave a partially-acquired set dangling.
            await set.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AcquireOneAsync(NormalizedPath path, JobId owner, CancellationToken ct)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out LockEntry? entry))
            {
                _entries[path] = new LockEntry { Holder = owner };
                return;
            }

            waiter = new Waiter(owner, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            entry.Waiters.Enqueue(waiter);
        }

        // Cancellation completes our TCS as canceled; the releaser skips already-completed waiters
        // so a cancelled waiter never wrongly takes ownership.
        await using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), waiter.Tcs).ConfigureAwait(false))
            await waiter.Tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Non-blocking acquire of one extra path while already holding a set — used only for
    /// RenameSuffix candidate probing (§4.6). Never waits, so it cannot create a wait cycle.</summary>
    public bool TryAcquireAdditional(PathLockSet held, NormalizedPath path)
    {
        ArgumentNullException.ThrowIfNull(held);
        lock (_gate)
        {
            if (_entries.ContainsKey(path))
                return false;
            _entries[path] = new LockEntry { Holder = held.Owner };
        }
        held.AddAcquired(path);
        return true;
    }

    internal void Release(NormalizedPath path, JobId owner)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out LockEntry? entry))
                return;

            // Transfer ownership to the next live waiter; drop waiters cancelled while queued.
            while (entry.Waiters.TryDequeue(out Waiter? next))
            {
                entry.Holder = next.Owner;
                if (next.Tcs.TrySetResult())
                    return;
            }
            _entries.Remove(path);
        }
    }
}

/// <summary>Releases all held paths (reverse order) on dispose. A Job holds exactly one set for its lifetime.</summary>
public sealed class PathLockSet(PathLockRegistry registry, JobId owner) : IAsyncDisposable
{
    private readonly List<NormalizedPath> _paths = [];
    private bool _disposed;

    internal JobId Owner => owner;

    public IReadOnlyList<NormalizedPath> Paths => _paths;

    internal void AddAcquired(NormalizedPath path) => _paths.Add(path);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        // Reverse order mirrors the ordered acquire — symmetric, though correctness does not
        // require it (releases never block).
        for (int i = _paths.Count - 1; i >= 0; i--)
            registry.Release(_paths[i], owner);
        return ValueTask.CompletedTask;
    }
}
