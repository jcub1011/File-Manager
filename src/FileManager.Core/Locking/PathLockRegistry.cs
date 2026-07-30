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
        // Honour cancellation even for an uncontended path — otherwise a cancelled caller could still
        // be handed a fully-acquired set when no path happened to be contended.
        ct.ThrowIfCancellationRequested();

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
            // Already ours → succeed idempotently, and deliberately do NOT re-add: a duplicate entry
            // would make DisposeAsync release the same path twice, handing it to a waiter while this
            // job still believes it holds it.
            //
            // This case is normal, not exceptional. A job's lock set already contains every
            // prospective final path (§4.3 step 1), so a RenameSuffix probe of the desired name
            // always hits a lock the probing job itself owns. Returning false here made
            // RenameSuffix skip the free desired name and place at "name (1).ext" on a first,
            // collision-free run — and, because the desired name then stayed empty, every
            // re-delivery suffixed again and grew the target set without bound.
            if (held.HoldsPath(path))
                return true;
            if (_entries.ContainsKey(path))
                return false;
            _entries[path] = new LockEntry { Holder = held.Owner };
            held.AddAcquired(path);
            return true;
        }
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
/// <summary>The set of paths one job holds. Guarded internally because per-target placement runs
/// bounded-parallel (§4.3 step 6), so several target tasks probe and extend the same set at once.</summary>
public sealed class PathLockSet(PathLockRegistry registry, JobId owner) : IAsyncDisposable
{
    private readonly List<NormalizedPath> _paths = [];
    private readonly object _pathsGate = new();
    private bool _disposed;

    internal JobId Owner => owner;

    /// <summary>A snapshot — the live list is mutated by concurrent target tasks.</summary>
    public IReadOnlyList<NormalizedPath> Paths
    {
        get { lock (_pathsGate) return _paths.ToArray(); }
    }

    internal bool HoldsPath(NormalizedPath path)
    {
        lock (_pathsGate) return _paths.Contains(path);
    }

    internal void AddAcquired(NormalizedPath path)
    {
        lock (_pathsGate) _paths.Add(path);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        // Snapshot under our own lock and release OUTSIDE it: Release takes the registry's gate, and
        // TryAcquireAdditional takes the registry gate then ours — holding both here in the opposite
        // order would be a lock-order inversion.
        NormalizedPath[] toRelease;
        lock (_pathsGate)
        {
            toRelease = [.. _paths];
            _paths.Clear();
        }
        // Reverse order mirrors the ordered acquire — symmetric, though correctness does not
        // require it (releases never block).
        for (int i = toRelease.Length - 1; i >= 0; i--)
            registry.Release(toRelease[i], owner);
        return ValueTask.CompletedTask;
    }
}
