using FileManager.Contracts.Primitives;
using FileManager.Core;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using FileManager.Core.Preflight;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Preflight;

public sealed class DiskPreflightTests : IDisposable
{
    private readonly string _dir;

    public DiskPreflightTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JobPlan Plan(out FakeVolumeInfoProvider volumes)
    {
        string source = Path.Combine(_dir, "src.bin");
        File.WriteAllBytes(source, new byte[1024]);
        string target = Path.Combine(_dir, "target", "src.bin");
        JobExecution execution = JobFixtures.Execution(source, _dir, [target], [Path.Combine(_dir, "target")]);
        volumes = new FakeVolumeInfoProvider();
        return execution.Plan;
    }

    [Fact]
    public void Sufficient_space_reports_all_volumes_ok()
    {
        JobPlan plan = Plan(out FakeVolumeInfoProvider volumes);
        var preflight = new DiskPreflight(volumes, new EngineConfig(), NullLogger<DiskPreflight>.Instance);

        Result<DiskPreflightReport, JobError> result = preflight.Evaluate(plan);
        Assert.True(result.TryGetValue(out DiskPreflightReport? report));
        Assert.All(report.Volumes, v => Assert.True(v.Sufficient));
    }

    [Fact]
    public void Insufficient_space_fails_with_InsufficientDiskSpace()
    {
        JobPlan plan = Plan(out FakeVolumeInfoProvider volumes);
        volumes.Free = 10;   // far below source size + 64 MiB margin
        var preflight = new DiskPreflight(volumes, new EngineConfig(), NullLogger<DiskPreflight>.Instance);

        Result<DiskPreflightReport, JobError> result = preflight.Evaluate(plan);
        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.InsufficientDiskSpace, error.Code);
    }

    [Fact]
    public void No_transformers_means_zero_workspace_need()
    {
        JobPlan plan = Plan(out FakeVolumeInfoProvider volumes);
        // Margin only just fits the single target's source-sized write; if workspace need were
        // counted it would double and overflow.
        volumes.Free = 1024 + new EngineConfig().PreflightSafetyMarginBytes;
        var preflight = new DiskPreflight(volumes, new EngineConfig(), NullLogger<DiskPreflight>.Instance);

        Result<DiskPreflightReport, JobError> result = preflight.Evaluate(plan);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Targets_on_distinct_volumes_sum_required_bytes_per_volume()
    {
        string source = Path.Combine(_dir, "src.bin");
        File.WriteAllBytes(source, new byte[1024]);
        string a1 = Path.Combine(_dir, "volA", "a1.bin");
        string a2 = Path.Combine(_dir, "volA", "a2.bin");
        string b1 = Path.Combine(_dir, "volB", "b1.bin");
        JobExecution execution = JobFixtures.Execution(
            source, _dir,
            [a1, a2, b1],
            [Path.Combine(_dir, "volA"), Path.Combine(_dir, "volA"), Path.Combine(_dir, "volB")]);

        var volumes = new MappedVolumeProvider(
            ("volA", "vol-a", long.MaxValue / 2),
            ("volB", "vol-b", long.MaxValue / 2));
        var preflight = new DiskPreflight(volumes, new EngineConfig(), NullLogger<DiskPreflight>.Instance);

        Result<DiskPreflightReport, JobError> result = preflight.Evaluate(execution.Plan);
        Assert.True(result.TryGetValue(out DiskPreflightReport? report));

        VolumeEstimate volA = report!.Volumes.Single(v => v.VolumeRoot == "vol-a");
        VolumeEstimate volB = report.Volumes.Single(v => v.VolumeRoot == "vol-b");
        Assert.Equal(2 * 1024, volA.RequiredBytes);   // two targets summed onto one volume
        Assert.Equal(1024, volB.RequiredBytes);       // one target on the other
    }

    [Fact]
    public void Unresolvable_target_volume_fails_closed_with_InsufficientDiskSpace()
    {
        string source = Path.Combine(_dir, "src.bin");
        File.WriteAllBytes(source, new byte[1024]);
        string target = Path.Combine(_dir, "unresolvable", "out.bin");
        JobExecution execution = JobFixtures.Execution(
            source, _dir, [target], [Path.Combine(_dir, "unresolvable")]);

        // Fails closed: a target path whose volume key cannot be resolved is an error, not a skip.
        var volumes = new UnresolvableTargetVolumeProvider();
        var preflight = new DiskPreflight(volumes, new EngineConfig(), NullLogger<DiskPreflight>.Instance);

        Result<DiskPreflightReport, JobError> result = preflight.Evaluate(execution.Plan);

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.InsufficientDiskSpace, error.Code);
    }

    /// <summary>Maps a path to a volume key + free bytes by a case-insensitive marker in its full
    /// path; unmatched paths (e.g. the workspace) fall to an abundant "vol-default".</summary>
    private sealed class MappedVolumeProvider : IVolumeInfoProvider
    {
        private readonly (string Marker, string Key, long Free)[] _map;

        public MappedVolumeProvider(params (string Marker, string Key, long Free)[] map) => _map = map;

        private (string Key, long Free)? Match(string path)
        {
            string full = Path.GetFullPath(path);
            foreach ((string marker, string key, long free) in _map)
                if (full.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return (key, free);
            return null;
        }

        public Result<string, string> GetVolumeKey(string path) =>
            Match(path) is { } m ? Result<string, string>.Success(m.Key) : Result<string, string>.Success("vol-default");

        public Result<long, string> GetAvailableFreeBytes(string path) =>
            Match(path) is { } m ? Result<long, string>.Success(m.Free) : Result<long, string>.Success(long.MaxValue / 2);

        public bool IsNetworkPath(string path) => false;
    }

    /// <summary>Resolves every path except one containing "unresolvable", for which the volume key
    /// lookup fails — driving the fail-closed branch.</summary>
    private sealed class UnresolvableTargetVolumeProvider : IVolumeInfoProvider
    {
        public Result<long, string> GetAvailableFreeBytes(string path) =>
            Result<long, string>.Success(long.MaxValue / 2);

        public Result<string, string> GetVolumeKey(string path) =>
            Path.GetFullPath(path).Contains("unresolvable", StringComparison.OrdinalIgnoreCase)
                ? Result<string, string>.Failure("no volume for this path")
                : Result<string, string>.Success(Path.GetPathRoot(Path.GetFullPath(path))?.ToLowerInvariant() ?? path);

        public bool IsNetworkPath(string path) => false;
    }
}
