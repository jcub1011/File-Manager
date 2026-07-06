using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System.Collections.Generic;

namespace FileManager.Core.Preflight;

public interface IDiskPreflight
{
    Result<DiskPreflightReport, JobError> Evaluate(JobPlan plan);
}

public sealed record DiskPreflightReport
{
    public required IReadOnlyList<VolumeEstimate> Volumes { get; init; }
}

public sealed record VolumeEstimate
{
    public required string VolumeRoot { get; init; }
    public required long RequiredBytes { get; init; }
    public required long AvailableBytes { get; init; }
    public required long SafetyMarginBytes { get; init; }
    public bool Sufficient => AvailableBytes >= RequiredBytes + SafetyMarginBytes;
}
