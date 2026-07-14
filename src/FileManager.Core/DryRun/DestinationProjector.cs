using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>Read-only (I-DRYRUN-RO) sweep of the profile's target roots that discovers pre-existing
/// files no source writes to and classifies each: left in place
/// (<see cref="OperationKind.Untouched"/>), an orphan a real <c>SyncMode.Mirror</c> run would delete
/// (<see cref="OperationKind.Deleted"/>), or a reparse point the sweep declines to judge
/// (<see cref="OperationKind.Unknown"/>). The per-source-file phase already emits every destination
/// write (New/Overwrite/Rename/Skip) as an operation, so this only fills the gaps that phase leaves.
/// Uses only <see cref="IFileSystemService.EnumerateEntries"/> (single-level, non-throwing,
/// read-only), so it mutates nothing.
///
/// The sweep is I/O-bound (each directory read blocks), so it is walked by dedicated threads sharing
/// a work-stealing directory queue — overlapping the blocking enumerations rather than issuing them
/// one at a time, and off the thread pool so a high worker count never starves it. The degree of
/// parallelism follows the target medium (local volumes are CPU/kernel-bound; network shares are
/// latency-bound and want heavy oversubscription) unless the profile pins a Manual worker count.
/// Results are collected unordered and then sorted by path in a final serial merge, so the output is
/// deterministic regardless of how the walk interleaved (and regardless of the worker count).
///
/// The two entry points let the caller choose how survivors are collected: <see cref="Project"/>
/// takes the full destination-operations list (batched path, unit tests), while
/// <see cref="AccumulateSurvivors"/> + <see cref="Sweep"/> let a streaming caller feed operation
/// chunks incrementally and retain only the (small) survivor path set.</summary>
public sealed class DestinationProjector(
    ILogger<DestinationProjector> logger, IFileSystemService fileSystem, IVolumeInfoProvider volumes)
{
    /// <summary>Adds every resulting destination path a batch of destination operations accounts for
    /// to <paramref name="survivors"/> — so the sweep never re-reports a path a source already writes
    /// to (or the pre-existing file a rename was routed around, which the engine emits as an explicit
    /// Untouched op). Safe to call repeatedly across streamed chunks.</summary>
    public static void AccumulateSurvivors(ISet<NormalizedPath> survivors, IReadOnlyList<VirtualFileOperation> destinationOperations)
    {
        ArgumentNullException.ThrowIfNull(survivors);
        ArgumentNullException.ThrowIfNull(destinationOperations);
        foreach (VirtualFileOperation op in destinationOperations)
            AddNormalized(survivors, op.Path);
    }

    /// <summary>Convenience for the batched path and unit tests: builds the survivor set from the full
    /// destination-operations list, then sweeps.</summary>
    public DestinationSweepResult Project(
        Profile profile, IReadOnlyList<VirtualFileOperation> destinationOperations, bool truncated, int? manualWorkers, CancellationToken ct)
    {
        HashSet<NormalizedPath> survivors = [];
        AccumulateSurvivors(survivors, destinationOperations);
        return Sweep(profile, survivors, truncated, manualWorkers, ct);
    }

    /// <summary>Sweeps the profile's target roots and classifies each pre-existing file not in
    /// <paramref name="survivors"/>. Each returned op's <see cref="VirtualFileOperation.SubjectIndex"/>
    /// indexes into the returned <see cref="DestinationSweepResult.Files"/> (op[i] → file[i]); a caller
    /// merging into a larger report offsets by the destination files already collected. The walk runs
    /// on dedicated threads whose count follows the target medium, or <paramref name="manualWorkers"/>
    /// when pinned; the result order is deterministic regardless.</summary>
    /// <param name="truncated">When the source pass was cut short, the survivor set is a prefix, so
    /// every "no source writes here" judgement is untrustworthy — a file we'd call an orphan (or
    /// Untouched) may well be written by an un-evaluated source. In that case we emit NOTHING rather
    /// than fabricate deletions/untouched entries.</param>
    /// <param name="manualWorkers">A pinned worker count (Manual concurrency), or null to auto-scale
    /// the degree of parallelism to the target medium (local vs. network).</param>
    /// <param name="maxEntries">Best-effort upper bound on emitted entries — workers stop feeding the
    /// sink once it is crossed and the merge trims to exactly this many, marking the result capped.</param>
    public DestinationSweepResult Sweep(
        Profile profile, ISet<NormalizedPath> survivors, bool truncated, int? manualWorkers, CancellationToken ct, int maxEntries = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(survivors);

        if (truncated)
            return new DestinationSweepResult([], []);

        // Normalize source roots once, for the target-under-source exclusion: a target root may
        // legally contain the source files (the validator only warns on overlap), and those source
        // files must never be previewed as destination deletions.
        List<NormalizedPath> sourceRoots = [];
        foreach (SourceConfig source in profile.Sources)
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                sourceRoots.Add(root);

        bool mirror = profile.SyncMode == SyncMode.Mirror;

        // Seed the shared work queue with each valid target root (paired with itself so every file
        // enumerated beneath it records that root). Overlapping roots (one nested under another) may
        // enqueue a subtree twice; the merge dedups by path.
        SweepState state = new();
        foreach (TargetConfig target in profile.Targets)
            if (NormalizedPath.Create(target.Path).TryGetValue(out NormalizedPath targetRoot))
                state.Enqueue(new WorkItem(targetRoot.Value, targetRoot));

        if (state.IsEmpty)
            return new DestinationSweepResult([], []);

        // The sweep blocks on directory enumeration, so run it on dedicated (LongRunning) threads
        // rather than starving the thread pool — essential at the high worker counts network shares
        // want. A Manual concurrency setting is honoured as-is; Automatic scales to the target medium.
        int dop = Math.Max(1, manualWorkers ?? AutoSweepWorkers(profile));

        // Each thread drains the shared queue: enumerate a directory (the blocking I/O), classify its
        // files into the shared sink, push its subdirectories back. Cancellation stays cooperative (no
        // token wired to the threads → no throw): a cancelled/capped item is drained without
        // processing, so `outstanding` still reaches zero and every thread exits.
        void Drain()
        {
            SpinWait spin = default;
            while (true)
            {
                if (state.TryTake(out WorkItem item))
                {
                    try
                    {
                        if (!ct.IsCancellationRequested && !state.Capped)
                            Walk(item, state, survivors, sourceRoots, mirror, maxEntries);
                    }
                    finally
                    {
                        state.Done();   // must run even on an unexpected throw, or peers spin forever
                    }
                    spin = default;      // found work — reset the idle backoff
                    continue;
                }

                if (state.AllDrained)
                    break;
                spin.SpinOnce();   // empty for now but a peer may still push; back off (spins → sleeps)
            }
        }

        Task[] threads = new Task[dop];
        for (int i = 0; i < dop; i++)
            threads[i] = Task.Factory.StartNew(
                Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task.WaitAll(threads);

        return Merge(state, maxEntries);
    }

    /// <summary>Enumerates one directory (single level): pushes descendable subdirectories back onto
    /// the queue and adds each classifiable file to the sink. Mirrors the source scanner's walk but
    /// with two deliberate asymmetries: (1) NO MaxDepth pruning — a true mirror deletes deep orphans
    /// regardless of the source's depth filter; (2) reparse-point directories are not descended
    /// (junctions can loop or escape the tree) and reparse-point files are classified Unknown, never
    /// Deleted.</summary>
    private void Walk(
        WorkItem item,
        SweepState state,
        ISet<NormalizedPath> survivors,
        List<NormalizedPath> sourceRoots,
        bool mirror,
        int maxEntries)
    {
        foreach (Result<FileSystemEntry, EnumerationFault> entry in fileSystem.EnumerateEntries(item.Dir))
        {
            if (entry.TryGetError(out EnumerationFault fault))
            {
                // A missing/unopenable target root or subdirectory is not a dry-run failure —
                // there is simply nothing (more) to report there. Log and move on.
                if (fault.Severity == EnumerationSeverity.Fatal)
                {
                    logger.LogDebug(
                        "Destination sweep stopped enumerating {Directory}: {Message}",
                        item.Dir, fault.Message);
                    break;   // Fatal is this directory's terminal item
                }
                logger.LogDebug("Destination sweep skipped an entry under {Directory}: {Message}",
                    item.Dir, fault.Message);
                continue;
            }

            entry.TryGetValue(out FileSystemEntry? fsEntry);
            bool isReparse = (fsEntry!.Attributes & FileAttributes.ReparsePoint) != 0;

            if (fsEntry.IsDirectory)
            {
                if (InfrastructurePaths.IsInfrastructureDirectoryName(fsEntry.FileName))
                    continue;
                if (isReparse)
                    continue;   // never descend a junction/symlink dir — loop / escape risk
                state.Enqueue(new WorkItem(fsEntry.FullPath, item.Root));
                continue;
            }

            // Files only — directory/empty-dir deletion is not modeled.
            if (InfrastructurePaths.IsTempFileName(fsEntry.FileName))
                continue;
            if (!NormalizedPath.Create(fsEntry.FullPath).TryGetValue(out NormalizedPath filePath))
                continue;
            if (survivors.Contains(filePath))
                continue;   // a source writes here — already an operation from the file phase
            if (IsUnderAnySource(filePath, sourceRoots))
                continue;   // a source file that happens to live under a target root

            // Best-effort budget: once the sink is full, stop feeding it (peers see Capped and drain
            // fast). The authoritative cap is applied in Merge, which trims to exactly maxEntries.
            if (!state.TryReserve(maxEntries))
                continue;

            OperationKind kind = isReparse
                ? OperationKind.Unknown              // can't judge a reparse point
                : mirror
                    ? OperationKind.Deleted          // orphan a mirror would remove
                    : OperationKind.Untouched;       // pre-existing, left in place

            state.Results.Add(new Candidate(
                filePath,
                new PhysicalFile
                {
                    Path = fsEntry.FullPath,
                    Root = item.Root.Value,
                    Length = fsEntry.Size,
                    LastWritten = fsEntry.Modified,
                    IsReparsePoint = isReparse,
                },
                kind,
                isReparse ? "reparse point (symlink/junction)" : null));
        }
    }

    /// <summary>Serial merge of the concurrently-collected candidates into the index-paired result:
    /// sorts by path for deterministic output, dedups overlapping roots, and assigns each op's
    /// <see cref="VirtualFileOperation.SubjectIndex"/> so <c>Ops[i]</c> references <c>Files[i]</c>.
    /// Applies the authoritative <paramref name="maxEntries"/> cap.</summary>
    private static DestinationSweepResult Merge(SweepState state, int maxEntries)
    {
        List<Candidate> collected = new(state.Results);
        collected.Sort(static (a, b) =>
            string.Compare(a.Path.Value, b.Path.Value, StringComparison.OrdinalIgnoreCase));

        List<PhysicalFile> files = new(collected.Count);
        List<VirtualFileOperation> ops = new(collected.Count);
        // Dedup across target roots that overlap (one nested under another) so a file enumerated
        // twice is reported once.
        HashSet<NormalizedPath> reported = [];
        bool capped = state.Capped;
        foreach (Candidate candidate in collected)
        {
            if (!reported.Add(candidate.Path))
                continue;
            if (files.Count >= maxEntries)
            {
                capped = true;   // more survived than the budget allows — trim here
                break;
            }

            int subjectIndex = files.Count;
            files.Add(candidate.File);
            ops.Add(new VirtualFileOperation
            {
                Path = candidate.File.Path,
                Root = candidate.File.Root,
                Kind = candidate.Kind,
                SourceIndex = -1,
                SubjectIndex = subjectIndex,
                Detail = candidate.Detail,
            });
        }
        return new DestinationSweepResult(files, ops, capped);
    }

    /// <summary>Degree of parallelism for the walk in Automatic mode. The sweep blocks on directory
    /// enumeration, so local volumes (CPU/kernel-bound) saturate at roughly the core count, while
    /// network shares (latency-bound) benefit from heavy oversubscription to overlap the round-trips.
    /// A single shared pool drains all target roots, so size to the most-latent target: any network
    /// root pushes the whole sweep to the oversubscribed count.</summary>
    private int AutoSweepWorkers(Profile profile)
    {
        foreach (TargetConfig target in profile.Targets)
            if (volumes.IsNetworkPath(target.Path))
                return Math.Clamp(Environment.ProcessorCount * 4, 16, 64);
        return Math.Max(1, Environment.ProcessorCount);
    }

    private static void AddNormalized(ISet<NormalizedPath> set, string path)
    {
        if (NormalizedPath.Create(path).TryGetValue(out NormalizedPath normalized))
            set.Add(normalized);
    }

    private static bool IsUnderAnySource(NormalizedPath path, List<NormalizedPath> sourceRoots)
    {
        foreach (NormalizedPath root in sourceRoots)
        {
            if (path.Equals(root) || path.IsUnder(root))
                return true;
        }
        return false;
    }

    /// <summary>A directory awaiting enumeration, tagged with the target root it descends from (so its
    /// files record the right <see cref="PhysicalFile.Root"/>).</summary>
    private readonly record struct WorkItem(string Dir, NormalizedPath Root);

    /// <summary>A classified survivor awaiting the merge. Holds everything both output records need;
    /// <see cref="VirtualFileOperation.SubjectIndex"/> is assigned only at merge time, so it is absent
    /// here.</summary>
    private readonly record struct Candidate(NormalizedPath Path, PhysicalFile File, OperationKind Kind, string? Detail);

    /// <summary>Shared state for the work-stealing walk. Encapsulates the directory queue, the result
    /// sink, the outstanding-work counter that drives termination, and the best-effort budget — all
    /// mutated concurrently, so every mutation is interlocked (mirrors the engine's RunCounters).</summary>
    private sealed class SweepState
    {
        public readonly ConcurrentBag<Candidate> Results = new();
        private readonly ConcurrentStack<WorkItem> _pending = new();
        // Directories queued OR being processed. Seeded/incremented on Enqueue, decremented on Done;
        // a worker exits only when it sees an empty stack AND this at zero (no peer can still push).
        private long _outstanding;
        private int _produced;   // survivors reserved against the budget
        private int _capped;      // 0/1 — budget exhausted (or a peer signalled it)

        public bool IsEmpty => _pending.IsEmpty;
        public bool Capped => Volatile.Read(ref _capped) != 0;
        public bool AllDrained => Interlocked.Read(ref _outstanding) == 0;

        public void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref _outstanding);
            _pending.Push(item);
        }

        public bool TryTake(out WorkItem item) => _pending.TryPop(out item);

        public void Done() => Interlocked.Decrement(ref _outstanding);

        /// <summary>Reserves one budget slot. Returns false (and latches Capped) once the reservations
        /// exceed <paramref name="maxEntries"/>; an unbounded budget always succeeds.</summary>
        public bool TryReserve(int maxEntries)
        {
            if (maxEntries == int.MaxValue)
                return true;
            if (Interlocked.Increment(ref _produced) > maxEntries)
            {
                Volatile.Write(ref _capped, 1);
                return false;
            }
            return true;
        }
    }
}
