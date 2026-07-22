using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
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

        // M:1 forces Flatten (spec §3.1.2); otherwise the profile's layout.
        bool flatten = profile.TargetLayout == TargetLayout.Flatten || profile.Sources.Count > 1;
        string fileName = Path.GetFileName(payload.SourcePath);
        string relativePath = Path.GetRelativePath(payload.SourceRoot, payload.SourcePath);

        List<TargetPlan> targets = new(profile.Targets.Count);
        for (int i = 0; i < profile.Targets.Count; i++)
        {
            string root = profile.Targets[i].Path;
            string prospective = flatten ? Path.Combine(root, fileName) : Path.Combine(root, relativePath);
            targets.Add(new TargetPlan { TargetIndex = i, TargetRoot = root, ProspectiveFinalPath = prospective });
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
        };
    }
}
