using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.DryRun;
using FileManager.Core.Platform;

namespace FileManager.Core.Tests.DryRun;

public sealed class DryRunSpaceEstimatorTests
{
    // A volume provider keyed by drive root, with per-volume capacity/cluster and optional failures.
    private sealed class FakeVolumes : IVolumeInfoProvider
    {
        public Dictionary<string, (long Total, long Free, long Cluster)> Caps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailCapacity { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>External <see cref="GetVolumeKey"/> calls — the estimator memoizes per root, so
        /// this should track distinct roots, not operations.</summary>
        public int VolumeKeyCalls { get; private set; }

        public Result<long, string> GetAvailableFreeBytes(string path) => long.MaxValue / 2;
        public bool IsNetworkPath(string path) => false;
        public DriveClass GetDriveClass(string path) => DriveClass.Fixed;

        public Result<string, string> GetVolumeKey(string path)
        {
            VolumeKeyCalls++;
            return Result<string, string>.Success(KeyOf(path));
        }

        // Uncounted key derivation so GetVolumeCapacity's internal lookup doesn't skew VolumeKeyCalls.
        private static string KeyOf(string path) =>
            (Path.GetPathRoot(Path.GetFullPath(path)) ?? path).TrimEnd('\\', '/').ToLowerInvariant();

        public Result<VolumeCapacity, string> GetVolumeCapacity(string path)
        {
            string key = KeyOf(path);
            if (FailCapacity.Contains(key))
                return Result<VolumeCapacity, string>.Failure("capacity unavailable");
            return Caps.TryGetValue(key, out (long Total, long Free, long Cluster) c)
                ? new VolumeCapacity(c.Total, c.Free, c.Cluster)
                : new VolumeCapacity(long.MaxValue / 2, long.MaxValue / 4, 1);
        }
    }

    private static PhysicalFile Pf(string path, string root, long length) =>
        new() { Path = path, Root = root, Length = length, LastWritten = DateTimeOffset.UnixEpoch };

    private static VirtualFileOperation Src(int index, string path, string root, OnSuccessAction? disp = null) =>
        new() { Path = path, Root = root, Kind = OperationKind.Processed, SourceIndex = index, SubjectIndex = -1, SourceDisposition = disp };

    private static VirtualFileOperation Dst(OperationKind kind, string path, string root, int sourceIndex = -1, int subjectIndex = -1) =>
        new() { Path = path, Root = root, Kind = kind, SourceIndex = sourceIndex, SubjectIndex = subjectIndex };

    private static VolumeSpaceEstimate Single(SpaceProjection p) => Assert.Single(p.Volumes);

    [Fact]
    public void New_writes_round_up_to_the_cluster_and_grow_settled_usage()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 800_000, Cluster: 4096);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        estimator.Accumulate(
            sourceFiles: [Pf(@"D:\src\a.dat", @"D:\src", 5000)],
            destinationFiles: [],
            sourceOperations: [Src(0, @"D:\src\a.dat", @"D:\src")],
            destinationOperations: [Dst(OperationKind.New, @"D:\dst\a.dat", @"D:\dst", sourceIndex: 0)],
            sourceBase: 0, destBase: 0, stageOverwrites: false);

        VolumeSpaceEstimate v = Single(estimator.Finalize(concurrency: 4));
        Assert.Equal("D:", v.VolumeRoot);
        Assert.Equal(5000, v.BytesWrittenBytes);          // raw I/O, unrounded
        Assert.Equal(8192, v.NetChangeBytes);             // 5000 rounded up to 2 clusters
        Assert.Equal(200_000, v.UsedNowBytes);            // total - free
        Assert.Equal(208_192, v.SettledUsedBytes);
        Assert.True(v.SettledUsedBytes <= v.RealisticPeakUsedBytes);
        Assert.True(v.RealisticPeakUsedBytes <= v.SafeCeilingUsedBytes);
    }

    [Fact]
    public void Overwrite_nets_new_minus_old_and_stages_the_retained_original_only_under_stage_overwrites()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 900_000, Cluster: 4096);

        static (PhysicalFile[] src, PhysicalFile[] dst, VirtualFileOperation[] sOps, VirtualFileOperation[] dOps) Scenario() =>
        (
            [Pf(@"D:\src\a.dat", @"D:\src", 5000)],
            [Pf(@"D:\dst\a.dat", @"D:\dst", 3000)],           // pre-existing file being overwritten
            [Src(0, @"D:\src\a.dat", @"D:\src")],
            [Dst(OperationKind.Overwrite, @"D:\dst\a.dat", @"D:\dst", sourceIndex: 0, subjectIndex: 0)]
        );

        (PhysicalFile[] src, PhysicalFile[] dst, VirtualFileOperation[] sOps, VirtualFileOperation[] dOps) = Scenario();
        var direct = new DryRunSpaceEstimator(volumes, 0);
        direct.Accumulate(src, dst, sOps, dOps, 0, 0, stageOverwrites: false);
        VolumeSpaceEstimate d = Single(direct.Finalize(1));
        Assert.Equal(8192 - 4096, d.NetChangeBytes);         // new(8192) - old(4096)

        var staged = new DryRunSpaceEstimator(volumes, 0);
        staged.Accumulate(src, dst, sOps, dOps, 0, 0, stageOverwrites: true);
        VolumeSpaceEstimate s = Single(staged.Finalize(1));
        Assert.Equal(8192 - 4096, s.NetChangeBytes);         // settled is unchanged by staging
        // The retained original (4096) coexists during a staged run, so the peak is higher than direct.
        Assert.True(s.RealisticPeakUsedBytes > d.RealisticPeakUsedBytes);
        Assert.Equal(4096, s.RealisticPeakUsedBytes - d.RealisticPeakUsedBytes);
    }

    // One orphan (10 KB) plus one incoming file (1 KB) on the same volume — enough for the deletion
    // timing to move the peak while leaving the at-rest total alone.
    private static DryRunSpaceEstimator MirrorScenario(FakeVolumes volumes, MirrorDeletion timing)
    {
        var estimator = new DryRunSpaceEstimator(volumes, 0, timing);
        estimator.Accumulate(
            sourceFiles: [Pf(@"C:\src\a.dat", @"C:\src", 1000)],
            destinationFiles: [Pf(@"D:\dst\orphan.dat", @"D:\dst", 10_000)],
            sourceOperations: [Src(0, @"C:\src\a.dat", @"C:\src")],
            destinationOperations:
            [
                Dst(OperationKind.New, @"D:\dst\a.dat", @"D:\dst", sourceIndex: 0),
                Dst(OperationKind.Deleted, @"D:\dst\orphan.dat", @"D:\dst", subjectIndex: 0),
            ],
            sourceBase: 0, destBase: 0, stageOverwrites: false);
        return estimator;
    }

    [Theory]
    [InlineData(MirrorDeletion.AfterCopy)]
    [InlineData(MirrorDeletion.Proactive)]
    public void Mirror_deletes_reduce_the_net_change_whichever_way_they_are_timed(MirrorDeletion timing)
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 500_000, Cluster: 1);

        VolumeSpaceEstimate v = Single(MirrorScenario(volumes, timing).Finalize(1));

        // Timing decides when the bytes come back, never whether they do: the volume ends the run
        // 9 KB lighter either way.
        Assert.Equal(1000 - 10_000, v.NetChangeBytes);
        Assert.Equal(1000, v.BytesWrittenBytes);
        Assert.Equal(500_000 - 9000, v.SettledUsedBytes);
    }

    [Fact]
    public void Deleting_after_the_copy_keeps_the_doomed_files_in_the_peak()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 500_000, Cluster: 1);

        VolumeSpaceEstimate v = Single(MirrorScenario(volumes, MirrorDeletion.AfterCopy).Finalize(1));

        // The orphan is still on disk while the copy lands, so the peak has to carry it: settled is
        // 9 KB below where the volume started, but usage on the way there climbs above it.
        Assert.Equal(10_000, v.DeferredReclaimBytes);
        Assert.Equal(10_000, v.MirrorDeferredReclaimBytes);
        Assert.Equal(v.SettledUsedBytes + 10_000 + 1000, v.RealisticPeakUsedBytes);
        Assert.True(v.RealisticPeakUsedBytes > v.UsedNowBytes);
    }

    [Fact]
    public void Deleting_proactively_takes_the_reclaimed_bytes_off_the_peak()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 500_000, Cluster: 1);

        VolumeSpaceEstimate after = Single(MirrorScenario(volumes, MirrorDeletion.AfterCopy).Finalize(1));
        VolumeSpaceEstimate pro = Single(MirrorScenario(volumes, MirrorDeletion.Proactive).Finalize(1));

        // Nothing is deferred: the orphan is gone before the copy starts.
        Assert.Equal(0, pro.DeferredReclaimBytes);
        Assert.Equal(0, pro.MirrorDeferredReclaimBytes);
        Assert.Equal(pro.SettledUsedBytes + 1000, pro.RealisticPeakUsedBytes);

        // Which is exactly the trade the setting offers: the same run, 10 KB lower at its peak.
        Assert.Equal(after.SettledUsedBytes, pro.SettledUsedBytes);
        Assert.Equal(10_000, after.RealisticPeakUsedBytes - pro.RealisticPeakUsedBytes);
        Assert.Equal(10_000, after.SafeCeilingUsedBytes - pro.SafeCeilingUsedBytes);
    }

    [Theory]
    [InlineData(MirrorDeletion.AfterCopy)]
    [InlineData(MirrorDeletion.Proactive)]
    public void The_peak_never_falls_below_the_settled_total(MirrorDeletion timing)
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 500_000, Cluster: 1);

        VolumeSpaceEstimate v = Single(MirrorScenario(volumes, timing).Finalize(1));

        Assert.True(v.SettledUsedBytes <= v.RealisticPeakUsedBytes);
        Assert.True(v.RealisticPeakUsedBytes <= v.SafeCeilingUsedBytes);
    }

    [Fact]
    public void Realistic_peak_is_concurrency_bounded_below_the_safe_ceiling()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 10_000_000, Free: 9_000_000, Cluster: 1);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        // 10 files of 100 bytes each; concurrency 2. Realistic temp ≈ 2×100; ceiling temp = 10×100.
        var src = Enumerable.Range(0, 10).Select(i => Pf($@"D:\src\f{i}.dat", @"D:\src", 100)).ToArray();
        var sOps = Enumerable.Range(0, 10).Select(i => Src(i, $@"D:\src\f{i}.dat", @"D:\src")).ToArray();
        var dOps = Enumerable.Range(0, 10).Select(i => Dst(OperationKind.New, $@"D:\dst\f{i}.dat", @"D:\dst", sourceIndex: i)).ToArray();

        estimator.Accumulate(src, [], sOps, dOps, 0, 0, stageOverwrites: false);
        VolumeSpaceEstimate v = estimator.Finalize(concurrency: 2).Volumes.Single();

        long settled = v.SettledUsedBytes;
        Assert.Equal(settled + 200, v.RealisticPeakUsedBytes);   // 2 concurrent temps × 100
        Assert.Equal(settled + 1000, v.SafeCeilingUsedBytes);    // all 10 temps coexist
    }

    [Fact]
    public void Writes_to_two_drives_produce_one_estimate_each()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 900_000, Cluster: 1);
        volumes.Caps["e:"] = (Total: 2_000_000, Free: 1_000_000, Cluster: 1);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        estimator.Accumulate(
            sourceFiles: [Pf(@"C:\src\a.dat", @"C:\src", 100), Pf(@"C:\src\b.dat", @"C:\src", 200)],
            destinationFiles: [],
            sourceOperations: [Src(0, @"C:\src\a.dat", @"C:\src"), Src(1, @"C:\src\b.dat", @"C:\src")],
            destinationOperations:
            [
                Dst(OperationKind.New, @"D:\dst\a.dat", @"D:\dst", sourceIndex: 0),
                Dst(OperationKind.New, @"E:\dst\b.dat", @"E:\dst", sourceIndex: 1),
            ],
            sourceBase: 0, destBase: 0, stageOverwrites: false);

        SpaceProjection p = estimator.Finalize(1);
        Assert.Equal(2, p.Volumes.Count);
        Assert.Equal(300, p.TotalBytesWritten);
        Assert.Equal(new[] { "D:", "E:" }, p.Volumes.Select(v => v.VolumeRoot).ToArray());
    }

    [Fact]
    public void A_volume_whose_capacity_query_fails_is_reported_without_capacity()
    {
        var volumes = new FakeVolumes();
        volumes.FailCapacity.Add("d:");
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        estimator.Accumulate(
            sourceFiles: [Pf(@"D:\src\a.dat", @"D:\src", 4096)],
            destinationFiles: [],
            sourceOperations: [Src(0, @"D:\src\a.dat", @"D:\src")],
            destinationOperations: [Dst(OperationKind.New, @"D:\dst\a.dat", @"D:\dst", sourceIndex: 0)],
            sourceBase: 0, destBase: 0, stageOverwrites: false);

        VolumeSpaceEstimate v = Single(estimator.Finalize(1));
        Assert.False(v.CapacityKnown);
        Assert.Equal(4096, v.BytesWrittenBytes);   // byte/net figures still reported
        Assert.Equal(4096, v.NetChangeBytes);      // cluster falls back to 1 (no rounding)
    }

    [Fact]
    public void Permanently_deleted_sources_free_space_only_on_a_volume_that_also_receives_writes()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 500_000, Cluster: 1);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        // Source on D: is permanently deleted; a write also lands on D:, so the freed bytes net out.
        estimator.Accumulate(
            sourceFiles: [Pf(@"D:\src\a.dat", @"D:\src", 1000)],
            destinationFiles: [],
            sourceOperations: [Src(0, @"D:\src\a.dat", @"D:\src", disp: OnSuccessAction.PermanentDelete)],
            destinationOperations: [Dst(OperationKind.New, @"D:\dst\a.dat", @"D:\dst", sourceIndex: 0)],
            sourceBase: 0, destBase: 0, stageOverwrites: false);

        VolumeSpaceEstimate v = Single(estimator.Finalize(1));
        Assert.Equal(1000, v.BytesWrittenBytes);
        Assert.Equal(0, v.NetChangeBytes);   // +1000 written, -1000 freed by the permanent delete

        // ...but only at rest. Disposition trails each file's own copy, so the original is still there
        // while the replacement is being written: the peak carries both, and no MirrorDeletion setting
        // changes that (this share of the deferred total is not attributed to Mirror).
        Assert.Equal(1000, v.DeferredReclaimBytes);
        Assert.Equal(0, v.MirrorDeferredReclaimBytes);
        Assert.Equal(v.SettledUsedBytes + 1000 + 1000, v.RealisticPeakUsedBytes);
    }

    [Fact]
    public void Per_folder_breakdown_appears_only_when_a_volume_has_multiple_target_roots()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 900_000, Cluster: 1);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        estimator.Accumulate(
            sourceFiles: [Pf(@"C:\s\a.dat", @"C:\s", 100), Pf(@"C:\s\b.dat", @"C:\s", 200)],
            destinationFiles: [],
            sourceOperations: [Src(0, @"C:\s\a.dat", @"C:\s"), Src(1, @"C:\s\b.dat", @"C:\s")],
            destinationOperations:
            [
                Dst(OperationKind.New, @"D:\one\a.dat", @"D:\one", sourceIndex: 0),
                Dst(OperationKind.New, @"D:\two\b.dat", @"D:\two", sourceIndex: 1),
            ],
            sourceBase: 0, destBase: 0, stageOverwrites: false);

        VolumeSpaceEstimate v = Single(estimator.Finalize(1));
        Assert.Equal(2, v.Folders.Count);
        Assert.Equal(new[] { @"D:\one", @"D:\two" }, v.Folders.Select(f => f.Root).ToArray());
        Assert.Equal(100, v.Folders.First(f => f.Root == @"D:\one").BytesWrittenBytes);
    }

    [Fact]
    public void Volume_key_is_resolved_once_per_distinct_root_and_same_volume_roots_share_a_tally()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 900_000, Cluster: 1);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        // Two streamed chunks, each writing under both D:\one and D:\two — four ops over two roots.
        for (int chunk = 0; chunk < 2; chunk++)
        {
            estimator.Accumulate(
                sourceFiles: [Pf(@"C:\s\a.dat", @"C:\s", 100), Pf(@"C:\s\b.dat", @"C:\s", 200)],
                destinationFiles: [],
                sourceOperations: [Src(0, @"C:\s\a.dat", @"C:\s"), Src(1, @"C:\s\b.dat", @"C:\s")],
                destinationOperations:
                [
                    Dst(OperationKind.New, @"D:\one\a.dat", @"D:\one", sourceIndex: 0),
                    Dst(OperationKind.New, @"D:\two\b.dat", @"D:\two", sourceIndex: 1),
                ],
                sourceBase: 0, destBase: 0, stageOverwrites: false);
        }

        // Both roots resolve to the one D: volume and share its tally...
        VolumeSpaceEstimate v = Single(estimator.Finalize(1));
        Assert.Equal(600, v.BytesWrittenBytes);
        // ...and the key normalization ran once per distinct root, not once per operation.
        Assert.Equal(2, volumes.VolumeKeyCalls);
    }
}
