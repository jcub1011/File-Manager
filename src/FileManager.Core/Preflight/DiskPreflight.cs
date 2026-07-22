using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FileManager.Core.Preflight;

/// <summary>Spec §4 Phase 1 free-space check, before any write. Groups all planned writes by
/// volume and requires, per volume V:
/// <c>required(V) = workspaceNeed(V) + Σ(target output sizes on V) + safetyMargin</c>, where the
/// estimated output size is the source size (best-effort) and workspaceNeed is 2× source when any
/// transformer emits a NewFile, 1× when InPlace-only, 0 with no transformers (§4.3).</summary>
public sealed class DiskPreflight(IVolumeInfoProvider volumes, EngineConfig config, ILogger<DiskPreflight> logger) : IDiskPreflight
{
    public Result<DiskPreflightReport, JobError> Evaluate(JobPlan plan)
    {
        long margin = config.PreflightSafetyMarginBytes;
        long sourceSize = plan.Source.SizeBytes;

        // volumeKey → (display root, representative path, required bytes)
        var required = new Dictionary<string, (string Root, string SamplePath, long Bytes)>(StringComparer.Ordinal);

        // Fail closed: a path whose volume we cannot resolve is an error, not a silent skip — skipping
        // it would let a write pass preflight with zero free-space checked on that volume.
        JobError? Add(string path, long bytes)
        {
            Result<string, string> key = volumes.GetVolumeKey(path);
            if (!key.TryGetValue(out string? volumeKey))
            {
                key.TryGetError(out string? keyError);
                return new JobError
                {
                    Code = JobErrorCode.InsufficientDiskSpace,
                    Message = $"could not resolve the volume for \"{path}\": {keyError}",
                    Path = path,
                };
            }
            if (required.TryGetValue(volumeKey, out (string Root, string SamplePath, long Bytes) v))
                required[volumeKey] = (v.Root, v.SamplePath, v.Bytes + bytes);
            else
                required[volumeKey] = (volumeKey, path, bytes);
            return null;
        }

        JobError? addError = Add(plan.WorkspaceDir, WorkspaceNeed(plan, sourceSize));
        if (addError is not null)
            return addError;
        foreach (TargetPlan target in plan.Targets)
        {
            addError = Add(target.ProspectiveFinalPath, sourceSize);
            if (addError is not null)
                return addError;
        }

        var estimates = new List<VolumeEstimate>(required.Count);
        var shortfalls = new List<string>();
        foreach ((string _, (string root, string samplePath, long bytes)) in required)
        {
            Result<long, string> available = volumes.GetAvailableFreeBytes(samplePath);
            if (!available.TryGetValue(out long free))
            {
                available.TryGetError(out string? err);
                return new JobError
                {
                    Code = JobErrorCode.InsufficientDiskSpace,
                    Message = $"could not determine free space on {root}: {err}",
                    Path = samplePath,
                };
            }

            var estimate = new VolumeEstimate
            {
                VolumeRoot = root,
                RequiredBytes = bytes,
                AvailableBytes = free,
                SafetyMarginBytes = margin,
            };
            estimates.Add(estimate);
            if (!estimate.Sufficient)
                shortfalls.Add($"{root}: need {bytes + margin:N0} B, have {free:N0} B");
        }

        var report = new DiskPreflightReport { Volumes = estimates };
        if (shortfalls.Count > 0)
        {
            logger.LogWarning("Disk preflight insufficient for job {JobId}: {Shortfalls}", plan.JobId.Short, string.Join("; ", shortfalls));
            return new JobError
            {
                Code = JobErrorCode.InsufficientDiskSpace,
                Message = $"insufficient disk space — {string.Join("; ", shortfalls)}",
            };
        }
        return report;
    }

    private static long WorkspaceNeed(JobPlan plan, long sourceSize)
    {
        IReadOnlyList<TransformerStep>? steps = plan.Profile.Transformers;
        if (steps is null || steps.Count == 0)
            return 0;                                   // no workspace — distribution streams from the source
        return steps.Any(s => s.OutputMode == OutputMode.NewFile) ? 2 * sourceSize : sourceSize;
    }
}
