using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using FileManager.Core.Scanning;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FileManager.Core.DryRun;

/// <summary>Read-only (I-DRYRUN-RO) sweep of the profile's target roots that discovers pre-existing
/// files no source writes to and classifies each: left in place
/// (<see cref="OperationKind.Untouched"/>), an orphan a real <c>SyncMode.Mirror</c> run would delete
/// (<see cref="OperationKind.Deleted"/>), or a reparse point the sweep declines to judge
/// (<see cref="OperationKind.Unknown"/>). The per-source-file phase already emits every destination
/// write (New/Overwrite/Rename/Skip) as an operation, so this only fills the gaps that phase leaves.
///
/// The sweep runs on the shared <see cref="IScanScheduler"/> (which owns the global and per-drive
/// thread budgets); this class supplies only policy via the session callbacks. Results are collected
/// unordered on the calling thread and then sorted by path in a final serial merge, so the output is
/// deterministic regardless of how the walk interleaved.
///
/// The two entry points let the caller choose how survivors are collected: <see cref="Project"/>
/// takes the full destination-operations list (batched path, unit tests), while
/// <see cref="AccumulateSurvivors"/> + <see cref="Sweep"/> let a streaming caller feed operation
/// chunks incrementally and retain only the (small) survivor path set.</summary>
public sealed class DestinationProjector(
    ILogger<DestinationProjector> logger, IVolumeInfoProvider volumes, IScanScheduler scheduler)
{
    /// <summary>Adds every resulting destination path a batch of destination operations accounts for
    /// to <paramref name="survivors"/> — so the sweep never re-reports a path a source already writes
    /// to (or the pre-existing file a rename was routed around, which the engine emits as an explicit
    /// Untouched op). Safe to call repeatedly across streamed chunks.</summary>
    public static void AccumulateSurvivors(ISet<NormalizedPath> survivors, IReadOnlyList<IFileOperationView> destinationOperations)
    {
        ArgumentNullException.ThrowIfNull(survivors);
        ArgumentNullException.ThrowIfNull(destinationOperations);
        foreach (IFileOperationView op in destinationOperations)
            AddNormalized(survivors, op.Path);
    }

    /// <summary>Convenience for the batched path and unit tests: builds the survivor set from the full
    /// destination-operations list, then sweeps.</summary>
    public DestinationSweepResult Project(
        Profile profile, IReadOnlyList<VirtualFileOperation> destinationOperations, bool truncated, CancellationToken ct)
    {
        HashSet<NormalizedPath> survivors = [];
        AccumulateSurvivors(survivors, destinationOperations);
        return Sweep(profile, survivors, truncated, ct);
    }

    /// <summary>Sweeps the profile's target roots and classifies each pre-existing file not in
    /// <paramref name="survivors"/>. Each returned op's <see cref="VirtualFileOperation.SubjectIndex"/>
    /// indexes into the returned <see cref="DestinationSweepResult.Files"/> (op[i] → file[i]); a caller
    /// merging into a larger report offsets by the destination files already collected. Concurrency and
    /// per-drive budgets are the scheduler's; the result order is deterministic regardless.</summary>
    /// <param name="truncated">When the source pass was cut short, the survivor set is a prefix, so
    /// every "no source writes here" judgement is untrustworthy — a file we'd call an orphan (or
    /// Untouched) may well be written by an un-evaluated source. In that case we emit NOTHING rather
    /// than fabricate deletions/untouched entries.</param>
    /// <param name="maxEntries">Best-effort upper bound on emitted entries — workers stop feeding the
    /// sink once it is crossed and the merge trims to exactly this many, marking the result capped.</param>
    /// <param name="progress">When supplied, its destination counter is incremented per classified
    /// file so a caller can sample it for live progress.</param>
    public DestinationSweepResult Sweep(
        Profile profile, ISet<NormalizedPath> survivors, bool truncated, CancellationToken ct,
        int maxEntries = int.MaxValue, DryRunProgressCounters? progress = null)
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

        // Collect target roots (paired with themselves so every file records its root). Overlapping
        // roots (one nested under another) may enumerate a subtree twice; the merge dedups by path —
        // but only needs to when there is more than one root.
        List<NormalizedPath> targetRoots = [];
        foreach (TargetConfig target in profile.Targets)
            if (NormalizedPath.Create(target.Path).TryGetValue(out NormalizedPath targetRoot))
                targetRoots.Add(targetRoot);

        if (targetRoots.Count == 0)
            return new DestinationSweepResult([], []);

        SweepBudget budget = new(maxEntries);
        ScanSessionOptions options = new()
        {
            // Deliberate asymmetries vs. the source scan: (1) NO MaxDepth pruning — a true mirror
            // deletes deep orphans regardless of the source's depth filter; (2) reparse-point dirs are
            // never descended (junctions can loop or escape the tree).
            OnSubdirectory = static (entry, tag) =>
            {
                if (InfrastructurePaths.IsInfrastructureDirectoryName(entry.FileName))
                    return new ChildDecision(false, null);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return new ChildDecision(false, null);
                return new ChildDecision(true, tag);
            },
            OnFile = (entry, _) =>
            {
                if (InfrastructurePaths.IsTempFileName(entry.FileName))
                    return false;
                // The enumerated path descends from a GetFullPath-canonicalized target root, so it is
                // itself canonical — wrap it WITHOUT paying Create's per-file re-canonicalization.
                NormalizedPath filePath = NormalizedPath.FromCanonical(entry.FullPath);
                if (survivors.Contains(filePath))
                    return false;   // a source writes here — already an operation from the file phase
                if (IsUnderAnySource(filePath, sourceRoots))
                    return false;   // a source file that happens to live under a target root
                // Best-effort budget: once crossed, stop emitting (Merge applies the authoritative cap).
                return budget.TryReserve();
            },
            // The sweep never surfaces faults as results (a missing/unopenable target root is simply
            // "nothing (more) to report there"); the scheduler still treats a Fatal as terminal for
            // its directory.
            OnFault = (fault, _) =>
            {
                logger.LogDebug("Destination sweep skipped/stopped an entry: {Message}", fault.Message);
                return null;
            },
        };

        Stopwatch walkWatch = Stopwatch.StartNew();
        List<Candidate> collected = [];
        using (IScanSession session = scheduler.OpenSession(options, ct))
        {
            foreach (NormalizedPath targetRoot in targetRoots)
            {
                (string key, DriveClass driveClass) = ResolveVolume(targetRoot.Value);
                session.Submit(new ScanWorkItem(targetRoot.Value, key, driveClass, targetRoot));
            }

            foreach (ScanResult result in session.Consume())
            {
                if (result.Entry is not FileSystemEntry fsEntry)
                    continue;
                NormalizedPath rootTag = (NormalizedPath)result.Tag!;
                bool isReparse = (fsEntry.Attributes & FileAttributes.ReparsePoint) != 0;
                NormalizedPath filePath = NormalizedPath.FromCanonical(fsEntry.FullPath);
                OperationKind kind = isReparse
                    ? OperationKind.Unknown              // can't judge a reparse point
                    : mirror
                        ? OperationKind.Deleted          // orphan a mirror would remove
                        : OperationKind.Untouched;       // pre-existing, left in place
                collected.Add(new Candidate(
                    filePath,
                    new PhysicalFile
                    {
                        Path = fsEntry.FullPath,
                        Root = rootTag.Value,
                        Length = fsEntry.Size,
                        LastWritten = fsEntry.Modified,
                        IsReparsePoint = isReparse,
                    },
                    kind,
                    isReparse ? "reparse point (symlink/junction)" : null));
                progress?.DestinationDiscovered();
            }
        }
        walkWatch.Stop();

        Stopwatch mergeWatch = Stopwatch.StartNew();
        DestinationSweepResult merged = Merge(collected, budget.Capped, maxEntries, dedup: targetRoots.Count > 1);
        mergeWatch.Stop();

        return merged with { WalkMs = walkWatch.ElapsedMilliseconds, MergeMs = mergeWatch.ElapsedMilliseconds };
    }

    /// <summary>Serial merge of the collected candidates into the index-paired result: sorts by path for
    /// deterministic output, dedups overlapping roots, and assigns each op's
    /// <see cref="VirtualFileOperation.SubjectIndex"/> so <c>Ops[i]</c> references <c>Files[i]</c>.
    /// Applies the authoritative <paramref name="maxEntries"/> cap.</summary>
    /// <param name="dedup">Whether paths can repeat across the walk (only when target roots overlap).
    /// With a single root no path is ever enumerated twice, so the per-path dedup set — a full extra
    /// hash of every survivor — is skipped.</param>
    private static DestinationSweepResult Merge(List<Candidate> collected, bool cappedDuringWalk, int maxEntries, bool dedup)
    {
        collected.Sort(static (a, b) =>
            string.Compare(a.Path.Value, b.Path.Value, StringComparison.OrdinalIgnoreCase));

        List<PhysicalFile> files = new(collected.Count);
        List<VirtualFileOperation> ops = new(collected.Count);
        // Dedup across target roots that overlap (one nested under another) so a file enumerated
        // twice is reported once. Only allocated/consulted when more than one root was swept.
        HashSet<NormalizedPath>? reported = dedup ? new(collected.Count) : null;
        bool capped = cappedDuringWalk;
        foreach (Candidate candidate in collected)
        {
            if (reported is not null && !reported.Add(candidate.Path))
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

    /// <summary>The volume key + drive class for a target root, used to size its per-drive scan
    /// budget. A key that cannot be resolved falls to a single synthetic local volume.</summary>
    private (string Key, DriveClass DriveClass) ResolveVolume(string path)
    {
        string key = volumes.GetVolumeKey(path).TryGetValue(out string? resolved) ? resolved : "local";
        return (key, volumes.GetDriveClass(path));
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

    /// <summary>A classified survivor awaiting the merge. Holds everything both output records need;
    /// <see cref="VirtualFileOperation.SubjectIndex"/> is assigned only at merge time, so it is absent
    /// here.</summary>
    private readonly record struct Candidate(NormalizedPath Path, PhysicalFile File, OperationKind Kind, string? Detail);

    /// <summary>The best-effort emission budget shared by the walk's workers: reserves a slot per
    /// candidate, latching Capped once the reservations exceed the cap. An unbounded budget always
    /// succeeds.</summary>
    private sealed class SweepBudget(int maxEntries)
    {
        private int _produced;
        private int _capped;

        public bool Capped => Volatile.Read(ref _capped) != 0;

        public bool TryReserve()
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
