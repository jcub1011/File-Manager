using System;
using System.Collections.Generic;
using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Platform;

namespace FileManager.Core.DryRun;

/// <summary>Computes the byte-level <see cref="SpaceProjection"/> for a dry run: how much data would
/// move, how much each destination volume would grow at rest, and the bounded-maximum (peak) storage
/// during the run. Fed one streamed chunk at a time so nothing beyond small per-volume tallies is
/// buffered — a chunk's operations reference only that chunk's own files (bundles are never split
/// across chunks; indices are globalised by a per-chunk base), so <c>index − base</c> resolves
/// locally without retaining the whole graph.
/// <para>
/// One estimator instance per dry run (it holds mutable per-run state). Volume capacity/cluster is
/// queried at most once per distinct volume and cached; a failed query is fail-soft
/// (<see cref="VolumeSpaceEstimate.CapacityKnown"/> is false) rather than fatal.
/// </para></summary>
public sealed class DryRunSpaceEstimator
{
    private readonly IVolumeInfoProvider _volumes;
    private readonly long _safetyMargin;
    private readonly Dictionary<string, VolumeTally> _byVolume = new(StringComparer.Ordinal);
    // Memoizes root path → tally so the volume-key normalization (GetFullPath + GetPathRoot + lower)
    // runs once per distinct root instead of once per operation. The distinct roots of a run are the
    // profile's source/target roots — a handful — while operations number in the hundreds of thousands.
    // _byVolume stays canonical: two roots on one volume resolve to the same shared tally.
    private readonly Dictionary<string, VolumeTally> _byRoot = new(StringComparer.OrdinalIgnoreCase);

    public DryRunSpaceEstimator(IVolumeInfoProvider volumes, long safetyMarginBytes)
    {
        _volumes = volumes;
        _safetyMargin = safetyMarginBytes;
    }

    /// <summary>Folds one streamed chunk into the running tallies. <paramref name="sourceBase"/> /
    /// <paramref name="destBase"/> are the global index of this chunk's first source/destination file
    /// (the running counts <em>before</em> this chunk is appended). The destination sweep is fed as a
    /// standalone chunk with both bases 0 (its ops index its own file list directly).</summary>
    public void Accumulate(
        IReadOnlyList<PhysicalFile> sourceFiles,
        IReadOnlyList<PhysicalFile> destinationFiles,
        IReadOnlyList<VirtualFileOperation> sourceOperations,
        IReadOnlyList<VirtualFileOperation> destinationOperations,
        int sourceBase,
        int destBase,
        bool stageOverwrites)
    {
        foreach (VirtualFileOperation op in destinationOperations)
        {
            switch (op.Kind)
            {
                case OperationKind.New or OperationKind.Overwrite or OperationKind.Rename:
                {
                    long incoming = Length(sourceFiles, op.SourceIndex - sourceBase);
                    VolumeTally v = VolumeFor(op.Root);
                    long inc = RoundUp(incoming, v.Cluster);

                    v.HasDestinationActivity = true;
                    v.BytesWritten += incoming;
                    v.AllWritesRounded += inc;
                    v.MaxIncomingRounded = Math.Max(v.MaxIncomingRounded, inc);

                    long net = inc;
                    if (op.Kind == OperationKind.Overwrite && op.SubjectIndex >= 0)
                    {
                        long old = RoundUp(Length(destinationFiles, op.SubjectIndex - destBase), v.Cluster);
                        net = inc - old;
                        // Under StageOverwrites the prior file is retained under .fm_staging until the
                        // job ends, so every overwritten original coexists — a full sum, not
                        // concurrency-bounded.
                        if (stageOverwrites)
                            v.StagingRetention += old;
                    }
                    v.NetChange += net;

                    FolderTally f = v.Folder(op.Root);
                    f.BytesWritten += incoming;
                    f.NetChange += net;
                    f.FileCount++;
                    break;
                }
                case OperationKind.Deleted:
                {
                    if (op.SubjectIndex >= 0)
                    {
                        VolumeTally v = VolumeFor(op.Root);
                        v.HasDestinationActivity = true;
                        long old = RoundUp(Length(destinationFiles, op.SubjectIndex - destBase), v.Cluster);
                        v.NetChange -= old;
                        v.Folder(op.Root).NetChange -= old;
                    }
                    break;
                }
                // Untouched / SkipConflict / SkipUnchanged / Unknown: no byte change.
            }
        }

        foreach (VirtualFileOperation op in sourceOperations)
        {
            // The source original is only genuinely freed by a permanent delete. Move-to-trash keeps
            // it in the recycle bin (same volume) and move-to-archive may land on the same volume, so
            // counting either as freed would overstate the space that comes back — the unsafe
            // direction for a "will it fit" preview. Recorded per source volume; applied in Finalize
            // only to volumes that also receive writes, so a pure source drive is not reported.
            if (op.SourceDisposition == OnSuccessAction.PermanentDelete && op.SourceIndex >= 0)
            {
                PhysicalFile sf = sourceFiles[op.SourceIndex - sourceBase];
                VolumeTally v = VolumeFor(sf.Root);
                v.SourceFreed += RoundUp(sf.Length, v.Cluster);
            }
        }
    }

    /// <summary>Produces the projection. <paramref name="concurrency"/> is how many file placements
    /// run in parallel (the executor's worker count) — it bounds the transient temp copies counted in
    /// the realistic peak.</summary>
    public SpaceProjection Finalize(int concurrency)
    {
        int workers = Math.Max(1, concurrency);
        List<VolumeSpaceEstimate> volumes = [];
        long totalWritten = 0;
        long totalNet = 0;

        foreach (VolumeTally v in _byVolume.Values.Where(v => v.HasDestinationActivity)
                     .OrderBy(v => v.VolumeRoot, StringComparer.OrdinalIgnoreCase))
        {
            long net = v.NetChange - v.SourceFreed;
            long usedNow = v.CapacityKnown ? Math.Max(0, v.TotalCapacity - v.FreeNow) : 0;
            long settled = usedNow + net;

            // The transient temp copies (one per in-flight placement, released on rename) never exceed
            // every temp coexisting, so the concurrency-bounded term is clamped by the all-writes sum.
            long realisticTemp = Math.Min(v.AllWritesRounded, (long)workers * v.MaxIncomingRounded);
            long peak = settled + v.StagingRetention + realisticTemp;
            long ceiling = settled + v.StagingRetention + v.AllWritesRounded;

            List<FolderSpaceBreakdown> folders = v.Folders.Count <= 1
                ? []
                : v.Folders
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new FolderSpaceBreakdown
                    {
                        Root = kv.Key,
                        BytesWrittenBytes = kv.Value.BytesWritten,
                        NetChangeBytes = kv.Value.NetChange,
                        FileCount = kv.Value.FileCount,
                    })
                    .ToList();

            volumes.Add(new VolumeSpaceEstimate
            {
                VolumeRoot = v.VolumeRoot,
                CapacityKnown = v.CapacityKnown,
                TotalCapacityBytes = v.TotalCapacity,
                UsedNowBytes = usedNow,
                FreeNowBytes = v.FreeNow,
                ClusterBytes = v.Cluster,
                BytesWrittenBytes = v.BytesWritten,
                NetChangeBytes = net,
                SettledUsedBytes = settled,
                RealisticPeakUsedBytes = peak,
                SafeCeilingUsedBytes = ceiling,
                Folders = folders,
            });

            totalWritten += v.BytesWritten;
            totalNet += net;
        }

        return new SpaceProjection
        {
            Volumes = volumes,
            TotalBytesWritten = totalWritten,
            TotalNetChangeBytes = totalNet,
            SafetyMarginBytes = _safetyMargin,
        };
    }

    private static long Length(IReadOnlyList<PhysicalFile> files, int localIndex) =>
        localIndex >= 0 && localIndex < files.Count ? files[localIndex].Length : 0;

    private static long RoundUp(long length, long cluster)
    {
        if (length <= 0 || cluster <= 1)
            return Math.Max(0, length);
        // Ceiling division, overflow-safe for realistic file sizes.
        return ((length + cluster - 1) / cluster) * cluster;
    }

    private VolumeTally VolumeFor(string path)
    {
        if (_byRoot.TryGetValue(path, out VolumeTally? cached))
            return cached;

        string key = _volumes.GetVolumeKey(path).TryGetValue(out string? k) ? k : path;
        if (!_byVolume.TryGetValue(key, out VolumeTally? tally))
        {
            tally = new VolumeTally { VolumeRoot = DisplayRoot(key) };
            if (_volumes.GetVolumeCapacity(path).TryGetValue(out VolumeCapacity cap))
            {
                tally.CapacityKnown = true;
                tally.TotalCapacity = cap.TotalBytes;
                tally.FreeNow = cap.FreeBytes;
                tally.Cluster = Math.Max(1, cap.BytesPerCluster);
            }
            _byVolume[key] = tally;
        }
        _byRoot[path] = tally;
        return tally;
    }

    // The key is a lowercased drive/share root ("c:", "c:\", "\\srv\share"); present a drive letter
    // trimmed and upper-cased ("C:"), leaving UNC share roots as-is.
    private static string DisplayRoot(string key)
    {
        string trimmed = key.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed.ToUpperInvariant() : trimmed;
    }

    private sealed class VolumeTally
    {
        public required string VolumeRoot { get; init; }
        public long Cluster { get; set; } = 1;
        public bool CapacityKnown { get; set; }
        public long TotalCapacity { get; set; }
        public long FreeNow { get; set; }

        public bool HasDestinationActivity { get; set; }
        public long BytesWritten { get; set; }       // raw incoming (I/O)
        public long AllWritesRounded { get; set; }    // Σ rounded incoming over all writes
        public long MaxIncomingRounded { get; set; }
        public long NetChange { get; set; }           // rounded, signed (destination side)
        public long StagingRetention { get; set; }    // rounded old sizes held under .fm_staging
        public long SourceFreed { get; set; }         // rounded bytes freed by permanent-deleted sources

        private readonly Dictionary<string, FolderTally> _folders = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, FolderTally> Folders => _folders;

        public FolderTally Folder(string root)
        {
            if (!_folders.TryGetValue(root, out FolderTally? f))
                _folders[root] = f = new FolderTally();
            return f;
        }
    }

    private sealed class FolderTally
    {
        public long BytesWritten { get; set; }
        public long NetChange { get; set; }
        public int FileCount { get; set; }
    }
}
