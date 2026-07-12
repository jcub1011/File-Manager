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

/// <summary>Read-only (I-DRYRUN-RO) sweep of the profile's target roots that produces the
/// destination-only entries the Destinations view can't derive from the source-oriented
/// <see cref="DryRunFileResult.Targets"/>: pre-existing files no source writes to
/// (<see cref="DryRunDestinationDisposition.Untouched"/>), and — under <c>SyncMode.Mirror</c> —
/// orphans a real mirror run would delete (<see cref="DryRunDestinationDisposition.Deleted"/>).
///
/// The write side (New/Overwritten/Renamed) is fully present in <c>Files[].Targets</c> and derived
/// UI-side, so it is deliberately NOT re-emitted here — this only fills the gaps a source-oriented
/// report leaves. Uses only <see cref="IFileSystemService.EnumerateEntries"/> (single-level,
/// non-throwing, read-only), so it mutates nothing.
///
/// The two entry points let the caller choose how survivors are collected: <see cref="Project"/>
/// takes the full file list (batched path, unit tests), while <see cref="AccumulateSurvivors"/> +
/// <see cref="Sweep"/> let a streaming caller feed file chunks incrementally and retain only the
/// (small) survivor path set rather than every file object.</summary>
public sealed class DestinationProjector(ILogger<DestinationProjector> logger, IFileSystemService fileSystem)
{
    /// <summary>Adds every destination path a batch of source files accounts for to
    /// <paramref name="survivors"/>. Safe to call repeatedly across streamed chunks.</summary>
    public static void AccumulateSurvivors(ISet<NormalizedPath> survivors, IReadOnlyList<DryRunFileResult> files)
    {
        ArgumentNullException.ThrowIfNull(survivors);
        ArgumentNullException.ThrowIfNull(files);
        foreach (DryRunFileResult file in files)
        {
            foreach (DryRunTargetAction target in file.Targets)
            {
                AddNormalized(survivors, target.TargetPath);
                // A rename leaves the pre-existing file at TargetPath (the survivor that forced the
                // suffix) AND writes a new file at Detail (the suffixed final path). Both are
                // "accounted for" — without the Detail path the conflict winner would be swept up as
                // an orphan and mis-flagged Deleted.
                if (target.Kind == DryRunTargetKind.WouldRenameTo && target.Detail is not null)
                    AddNormalized(survivors, target.Detail);
            }
        }
    }

    /// <summary>Convenience for the batched path and unit tests: builds the survivor set from the
    /// full file list, then sweeps.</summary>
    public IReadOnlyList<DryRunDestinationEntry> Project(
        Profile profile, IReadOnlyList<DryRunFileResult> files, bool truncated, CancellationToken ct)
    {
        HashSet<NormalizedPath> survivors = [];
        AccumulateSurvivors(survivors, files);
        return Sweep(profile, survivors, truncated, ct);
    }

    /// <summary>Sweeps the profile's target roots and classifies each pre-existing file not in
    /// <paramref name="survivors"/>.</summary>
    /// <param name="truncated">When the source pass was cut short, the survivor set is a prefix, so
    /// every "no source writes here" judgement is untrustworthy — a file we'd call an orphan (or
    /// Untouched) may well be written by an un-evaluated source. In that case we emit NOTHING rather
    /// than fabricate deletions/untouched entries; the UI still shows the derived writes and surfaces
    /// the truncation banner.</param>
    public IReadOnlyList<DryRunDestinationEntry> Sweep(
        Profile profile, ISet<NormalizedPath> survivors, bool truncated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(survivors);

        if (truncated)
            return [];

        // Normalize source roots once, for the target-under-source exclusion: a target root may
        // legally contain the source files (the validator only warns on overlap), and those source
        // files must never be previewed as destination deletions.
        List<NormalizedPath> sourceRoots = [];
        foreach (SourceConfig source in profile.Sources)
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                sourceRoots.Add(root);

        bool mirror = profile.SyncMode == SyncMode.Mirror;

        List<DryRunDestinationEntry> entries = [];
        // Dedup across target roots that overlap (one nested under another) so a file enumerated
        // twice is reported once.
        HashSet<NormalizedPath> reported = [];
        foreach (TargetConfig target in profile.Targets)
        {
            if (ct.IsCancellationRequested)
                break;
            if (!NormalizedPath.Create(target.Path).TryGetValue(out NormalizedPath targetRoot))
                continue;
            SweepRoot(targetRoot, survivors, sourceRoots, mirror, entries, reported, ct);
        }
        return entries;
    }

    /// <summary>Explicit-stack DFS over a single target root, mirroring the source scanner's walk but
    /// with two deliberate asymmetries: (1) NO MaxDepth pruning — a true mirror deletes deep orphans
    /// regardless of the source's depth filter; (2) reparse-point directories are not descended
    /// (junctions can loop or escape the tree) and reparse-point files are classified Unknown, never
    /// Deleted.</summary>
    private void SweepRoot(
        NormalizedPath targetRoot,
        ISet<NormalizedPath> survivors,
        List<NormalizedPath> sourceRoots,
        bool mirror,
        List<DryRunDestinationEntry> entries,
        HashSet<NormalizedPath> reported,
        CancellationToken ct)
    {
        Stack<string> pending = new();
        pending.Push(targetRoot.Value);

        while (pending.Count > 0)
        {
            if (ct.IsCancellationRequested)
                return;
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
                    continue;   // a source writes here — derived UI-side from Files[].Targets
                if (IsUnderAnySource(filePath, sourceRoots))
                    continue;   // a source file that happens to live under a target root
                if (!reported.Add(filePath))
                    continue;   // already reported via an overlapping target root

                DryRunDestinationDisposition disposition = isReparse
                    ? DryRunDestinationDisposition.Unknown              // can't judge a reparse point
                    : mirror
                        ? DryRunDestinationDisposition.Deleted          // orphan a mirror would remove
                        : DryRunDestinationDisposition.Untouched;       // pre-existing, left in place

                entries.Add(new DryRunDestinationEntry
                {
                    TargetPath = item.FullPath,
                    TargetRoot = targetRoot.Value,
                    Disposition = disposition,
                    Detail = isReparse ? "reparse point (symlink/junction)" : null,
                });
            }
        }
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
