using FileManager.Contracts.Primitives;
using FileManager.Core;
using FileManager.Core.Jobs;
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
}
