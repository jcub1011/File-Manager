using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Profiles;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Jobs;

/// <summary>Builds a <see cref="JobPlan"/> from a matched (profile, payload) pair — the orchestrator's
/// hand-off to the executor. Target-path resolution mirrors the dry-run engine
/// (<see cref="DryRun.DryRunEngine"/>): M:1 and <see cref="TargetLayout.Flatten"/> layouts flatten to
/// the bare file name, <see cref="TargetLayout.PreserveStructure"/> keeps the path relative to the
/// source root. The workspace path is deterministic from the <see cref="JobId"/> alone (§7.2 row 2);
/// a no-transformer job creates nothing there.</summary>
public sealed class JobPlanFactory(EnginePaths paths, EngineConfig config)
{
    public Result<JobPlan, JobError> Build(Profile profile, Payload payload)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(payload);

        FileMetadata? metadata = payload.Metadata;
        if (metadata is null)
        {
            Result<FileMetadata?, string> read = FileMetadataReader.Read(payload.SourcePath);
            if (read.TryGetError(out string? statError))
                return new JobError { Code = JobErrorCode.SourceUnreadable, Message = statError, Path = payload.SourcePath };
            read.TryGetValue(out metadata);
            if (metadata is null)
                return new JobError { Code = JobErrorCode.SourceDisposed, Message = "source no longer exists", Path = payload.SourcePath };
        }

        JobId jobId = JobId.New();

        string relativePath = Path.GetRelativePath(payload.SourceRoot, payload.SourcePath);
        TargetPathLayout layout = TargetPathLayout.For(profile, payload.SourcePath, relativePath);

        List<TargetPlan> targets = new(profile.Targets.Count);
        for (int i = 0; i < profile.Targets.Count; i++)
        {
            string root = profile.Targets[i].Path;
            targets.Add(new TargetPlan { TargetIndex = i, TargetRoot = root, ProspectiveFinalPath = layout.Resolve(root) });
        }

        PolicySettings p = profile.Policies;
        PolicySnapshot policies = new()
        {
            Verification = p.VerificationMethod,
            OverwriteHandling = p.OverwriteHandling,
            ConflictResolution = p.ConflictResolution,
            OnSuccess = p.OnSuccess,
            ArchiveFolder = p.ArchiveFolder,
            MetadataOnConflict = p.MetadataOnConflict,
            LargeFileIdentity = p.LargeFileIdentity,
            LargeFileIdentityThresholdBytes = p.LargeFileIdentityThresholdBytes,
        };

        string tempRoot = string.IsNullOrWhiteSpace(config.TempRoot) ? paths.WorkDirectory : config.TempRoot!;
        string workspaceDir = Path.Combine(tempRoot, ".pipeline_tmp", jobId.Value.ToString("N"));

        return new JobPlan
        {
            JobId = jobId,
            ProfileId = profile.Id,
            Profile = profile,
            Payload = payload,
            Source = new SourceSnapshot { Path = payload.SourcePath, SizeBytes = metadata.Length, LastWriteUtc = metadata.LastWritten },
            SourceMetadata = metadata,
            Targets = targets,
            Policies = policies,
            WorkspaceDir = workspaceDir,
            SourceIndex = ResolveSourceIndex(profile, payload.SourceRoot),
        };
    }

    /// <summary>The payload's originating Source position within the profile, or -1 when its root
    /// matches none. THE single definition of the M:1 priority rank (spec §3.4) — public so plans built
    /// outside this factory (test fixtures, recovery) rank identically instead of re-deriving it.</summary>
    public static int ResolveSourceIndex(Profile profile, string sourceRoot)
    {
        if (!NormalizedPath.Create(sourceRoot).TryGetValue(out NormalizedPath root))
            return -1;
        for (int i = 0; i < profile.Sources.Count; i++)
            if (NormalizedPath.Create(profile.Sources[i].Path).TryGetValue(out NormalizedPath candidate) && candidate == root)
                return i;
        return -1;
    }
}
