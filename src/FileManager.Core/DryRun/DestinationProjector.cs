using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
/// The two entry points let the caller choose how survivors are collected: <see cref="Project"/>
/// takes the full destination-operations list (batched path, unit tests), while
/// <see cref="AccumulateSurvivors"/> + <see cref="Sweep"/> let a streaming caller feed operation
/// chunks incrementally and retain only the (small) survivor path set.</summary>
public sealed class DestinationProjector(ILogger<DestinationProjector> logger, IFileSystemService fileSystem)
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
        Profile profile, IReadOnlyList<VirtualFileOperation> destinationOperations, bool truncated, CancellationToken ct)
    {
        HashSet<NormalizedPath> survivors = [];
        AccumulateSurvivors(survivors, destinationOperations);
        return Sweep(profile, survivors, truncated, ct);
    }

    /// <summary>Sweeps the profile's target roots and classifies each pre-existing file not in
    /// <paramref name="survivors"/>. Each returned op's <see cref="VirtualFileOperation.SubjectIndex"/>
    /// indexes into the returned <see cref="DestinationSweepResult.Files"/> (op[i] → file[i]); a caller
    /// merging into a larger report offsets by the destination files already collected.</summary>
    /// <param name="truncated">When the source pass was cut short, the survivor set is a prefix, so
    /// every "no source writes here" judgement is untrustworthy — a file we'd call an orphan (or
    /// Untouched) may well be written by an un-evaluated source. In that case we emit NOTHING rather
    /// than fabricate deletions/untouched entries.</param>
    public DestinationSweepResult Sweep(
        Profile profile, ISet<NormalizedPath> survivors, bool truncated, CancellationToken ct, int maxEntries = int.MaxValue)
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

        List<PhysicalFile> files = [];
        List<VirtualFileOperation> ops = [];
        // Dedup across target roots that overlap (one nested under another) so a file enumerated
        // twice is reported once.
        HashSet<NormalizedPath> reported = [];
        bool capped = false;
        foreach (TargetConfig target in profile.Targets)
        {
            if (ct.IsCancellationRequested)
                break;
            if (files.Count >= maxEntries) { capped = true; break; }
            if (!NormalizedPath.Create(target.Path).TryGetValue(out NormalizedPath targetRoot))
                continue;
            if (SweepRoot(targetRoot, survivors, sourceRoots, mirror, files, ops, reported, maxEntries, ct))
            {
                capped = true;
                break;
            }
        }
        return new DestinationSweepResult(files, ops, capped);
    }

    /// <summary>Explicit-stack DFS over a single target root, mirroring the source scanner's walk but
    /// with two deliberate asymmetries: (1) NO MaxDepth pruning — a true mirror deletes deep orphans
    /// regardless of the source's depth filter; (2) reparse-point directories are not descended
    /// (junctions can loop or escape the tree) and reparse-point files are classified Unknown, never
    /// Deleted.</summary>
    /// <summary>Returns true if the entry cap was hit (the caller should stop sweeping further roots
    /// and mark the result truncated).</summary>
    private bool SweepRoot(
        NormalizedPath targetRoot,
        ISet<NormalizedPath> survivors,
        List<NormalizedPath> sourceRoots,
        bool mirror,
        List<PhysicalFile> files,
        List<VirtualFileOperation> ops,
        HashSet<NormalizedPath> reported,
        int maxEntries,
        CancellationToken ct)
    {
        Stack<string> pending = new();
        pending.Push(targetRoot.Value);

        while (pending.Count > 0)
        {
            if (ct.IsCancellationRequested)
                return false;
            string directory = pending.Pop();

            foreach (Result<FileSystemEntry, EnumerationFault> entry in fileSystem.EnumerateEntries(directory))
            {
                if (entry.TryGetError(out EnumerationFault fault))
                {
                    // A missing/unopenable target root or subdirectory is not a dry-run failure —
                    // there is simply nothing (more) to report there. Log and move on.
                    if (fault.Severity == EnumerationSeverity.Fatal)
                    {
                        logger.LogDebug(
                            "Destination sweep stopped enumerating {Directory}: {Message}",
                            directory, fault.Message);
                        break;   // Fatal is this directory's terminal item
                    }
                    logger.LogDebug("Destination sweep skipped an entry under {Directory}: {Message}",
                        directory, fault.Message);
                    continue;
                }

                entry.TryGetValue(out FileSystemEntry? item);
                bool isReparse = (item!.Attributes & FileAttributes.ReparsePoint) != 0;

                if (item.IsDirectory)
                {
                    if (InfrastructurePaths.IsInfrastructureDirectoryName(item.FileName))
                        continue;
                    if (isReparse)
                        continue;   // never descend a junction/symlink dir — loop / escape risk
                    pending.Push(item.FullPath);
                    continue;
                }

                // Files only — directory/empty-dir deletion is not modeled.
                if (InfrastructurePaths.IsTempFileName(item.FileName))
                    continue;
                if (!NormalizedPath.Create(item.FullPath).TryGetValue(out NormalizedPath filePath))
                    continue;
                if (survivors.Contains(filePath))
                    continue;   // a source writes here — already an operation from the file phase
                if (IsUnderAnySource(filePath, sourceRoots))
                    continue;   // a source file that happens to live under a target root
                if (!reported.Add(filePath))
                    continue;   // already reported via an overlapping target root

                if (files.Count >= maxEntries)
                    return true;   // hit the entry cap — caller marks the sweep truncated

                OperationKind kind = isReparse
                    ? OperationKind.Unknown              // can't judge a reparse point
                    : mirror
                        ? OperationKind.Deleted          // orphan a mirror would remove
                        : OperationKind.Untouched;       // pre-existing, left in place

                int subjectIndex = files.Count;
                files.Add(new PhysicalFile
                {
                    Path = item.FullPath,
                    Root = targetRoot.Value,
                    Length = item.Size,
                    LastWritten = item.Modified,
                    IsReparsePoint = isReparse,
                });
                ops.Add(new VirtualFileOperation
                {
                    Path = item.FullPath,
                    Root = targetRoot.Value,
                    Kind = kind,
                    SourceIndex = -1,
                    SubjectIndex = subjectIndex,
                    Detail = isReparse ? "reparse point (symlink/junction)" : null,
                });
            }
        }
        return false;
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
}
