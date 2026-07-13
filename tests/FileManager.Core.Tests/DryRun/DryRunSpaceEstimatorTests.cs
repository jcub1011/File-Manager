using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
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

        public Result<long, string> GetAvailableFreeBytes(string path) => long.MaxValue / 2;
        public bool IsNetworkPath(string path) => false;

        public Result<string, string> GetVolumeKey(string path)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return Result<string, string>.Success(root.TrimEnd('\\', '/').ToLowerInvariant());
        }

        public Result<VolumeCapacity, string> GetVolumeCapacity(string path)
        {
            GetVolumeKey(path).TryGetValue(out string? key);
            if (FailCapacity.Contains(key!))
                return Result<VolumeCapacity, string>.Failure("capacity unavailable");
            return Caps.TryGetValue(key!, out (long Total, long Free, long Cluster) c)
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

    [Fact]
    public void Mirror_deletes_reduce_the_net_change()
    {
        var volumes = new FakeVolumes();
        volumes.Caps["d:"] = (Total: 1_000_000, Free: 500_000, Cluster: 1);
        var estimator = new DryRunSpaceEstimator(volumes, 0);

        estimator.Accumulate(
            sourceFiles: [],
            destinationFiles: [Pf(@"D:\dst\orphan.dat", @"D:\dst", 10_000)],
            sourceOperations: [],
            destinationOperations: [Dst(OperationKind.Deleted, @"D:\dst\orphan.dat", @"D:\dst", subjectIndex: 0)],
            sourceBase: 0, destBase: 0, stageOverwrites: false);

        VolumeSpaceEstimate v = Single(estimator.Finalize(1));
        Assert.Equal(-10_000, v.NetChangeBytes);
        Assert.Equal(0, v.BytesWrittenBytes);
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
}
