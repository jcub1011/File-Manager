using System.Security.Cryptography;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Builds <see cref="JobExecution"/>/<see cref="JobPlan"/> graphs for placement, rollback,
/// and recovery tests without standing up the (deferred) orchestrator.</summary>
internal static class JobFixtures
{
    public static string Sha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static PolicySnapshot Policy(
        VerificationMethod verification = VerificationMethod.Sha256,
        OverwriteHandling overwrite = OverwriteHandling.StageOverwrites,
        OnSuccessAction onSuccess = OnSuccessAction.KeepSource,
        string? archiveFolder = null) => new()
    {
        Verification = verification,
        OverwriteHandling = overwrite,
        ConflictResolution = ConflictResolution.Overwrite,
        OnSuccess = onSuccess,
        ArchiveFolder = archiveFolder,
        MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
    };

    /// <summary>A single-target job whose sealed output is the source file itself (no transformers).</summary>
    public static JobExecution Execution(
        string sourcePath,
        string sourceRoot,
        IReadOnlyList<string> targetFinalPaths,
        IReadOnlyList<string> targetRoots,
        PolicySnapshot? policy = null,
        JobId? jobId = null)
    {
        policy ??= Policy();
        var fileInfo = new FileInfo(sourcePath);
        JobId id = jobId ?? JobId.New();

        var targets = new TargetPlan[targetFinalPaths.Count];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = new TargetPlan { TargetIndex = i, TargetRoot = targetRoots[i], ProspectiveFinalPath = targetFinalPaths[i] };

        Profile profile = TestProfiles.Valid(sourceRoot, targetRoots[0]);
        var source = new SourceSnapshot
        {
            Path = sourcePath,
            SizeBytes = fileInfo.Length,
            LastWriteUtc = File.GetLastWriteTimeUtc(sourcePath),
        };

        var plan = new JobPlan
        {
            JobId = id,
            ProfileId = profile.Id,
            Profile = profile,
            Payload = new Payload(profile.Id, sourcePath, sourceRoot, TriggerKind.ManualShell, DateTimeOffset.UtcNow),
            Source = source,
            SourceMetadata = new FileMetadata
            {
                Length = fileInfo.Length,
                LastWritten = source.LastWriteUtc,
                Created = File.GetCreationTimeUtc(sourcePath),
                IsHidden = false,
                IsSystem = false,
                IsSymlink = false,
            },
            Targets = targets,
            Policies = policy,
            WorkspaceDir = Path.Combine(Path.GetTempPath(), ".pipeline_tmp", id.Value.ToString("N")),
        };

        return new JobExecution
        {
            Plan = plan,
            Output = new SealedOutput
            {
                Path = sourcePath,
                SizeBytes = fileInfo.Length,
                Sha256 = policy.Verification == VerificationMethod.Sha256 ? Sha256Hex(sourcePath) : "",
                SourceLastWriteUtc = source.LastWriteUtc,
            },
        };
    }
}
