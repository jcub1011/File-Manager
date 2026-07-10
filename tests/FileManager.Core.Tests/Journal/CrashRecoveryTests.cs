using System.Security.Cryptography;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Placement;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Journal;

/// <summary>The §7.4 fault-injection matrix: hand-author a journal + on-disk artifact state that a
/// crash would leave, run <see cref="CrashRecovery.Recover"/>, and assert the matching §7.3 row
/// fired — never a disposed source with a missing copy (I-DISPOSE).</summary>
public sealed class CrashRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly EnginePaths _paths;
    private readonly JobJournal _journal;
    private readonly FakeTrashService _trash;
    private readonly CrashRecovery _recovery;

    public CrashRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new EnginePaths { Root = Path.Combine(_root, "engine") };
        var time = new FakeTimeProvider();
        var config = new EngineConfig { TempRoot = Path.Combine(_root, "work") };
        _journal = new JobJournal(_paths, config, NullLogger<JobJournal>.Instance);
        var hasher = new FileHasher(NullLogger<FileHasher>.Instance);
        var rollback = new RollbackExecutor(_journal, time, NullLogger<RollbackExecutor>.Instance);
        _trash = new FakeTrashService(Path.Combine(_root, "bin"));
        var disposition = new SourceDispositionService(_trash, new DispositionAuditLog(_paths, NullLogger<DispositionAuditLog>.Instance), time, NullLogger<SourceDispositionService>.Instance);
        _recovery = new CrashRecovery(_journal, hasher, rollback, disposition, _paths, config, time, NullLogger<CrashRecovery>.Instance);
    }

    public void Dispose()
    {
        _journal.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static string Sha(string content)
    {
        using var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private JobOpenedRecord Open(Guid job, string sourcePath, string finalPath, string targetRoot, string workspace, VerificationMethod verification, OnSuccessAction onSuccess)
        => new()
        {
            JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch,
            ProfileId = Guid.NewGuid(),
            Source = new SourceSnapshot { Path = sourcePath, SizeBytes = 0, LastWriteUtc = DateTimeOffset.UnixEpoch },
            Policies = new PolicySnapshot
            {
                Verification = verification, OverwriteHandling = OverwriteHandling.StageOverwrites,
                ConflictResolution = ConflictResolution.Overwrite, OnSuccess = onSuccess,
                MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
            },
            WorkspaceDir = workspace,
            Targets = [new TargetPlan { TargetIndex = 0, TargetRoot = targetRoot, ProspectiveFinalPath = finalPath }],
        };

    [Fact]
    public void Row_A_no_output_sealed_cleans_temps_and_closes_failed()
    {
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string final = Path.Combine(_root, "target", "out.dat");
        string temp = final + ".fmtmp-" + jobId.Short;
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllText(temp, "partial");

        _journal.Append(Open(job, Path.Combine(_root, "src.dat"), final, Path.Combine(_root, "target"), workspace, VerificationMethod.Sha256, OnSuccessAction.KeepSource));
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = false });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.CleanedPrePlacement);
        Assert.False(File.Exists(temp));               // pre-placement temp removed
        Assert.False(Directory.Exists(workspace));      // workspace removed
    }

    [Fact]
    public void Mid_placement_completes_forward_when_the_gate_passes()
    {
        // Row E: target-staged (prior moved out, final absent), temp present & verified against the
        // reference, Verification == Sha256 — the gate passes and recovery renames temp → final.
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        const string content = "the verified payload";
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        string final = Path.Combine(targetRoot, "out.dat");      // absent — moved to staging pre-crash
        string temp = final + ".fmtmp-" + jobId.Short;
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(temp, content);                        // verified temp awaiting rename
        string staged = Path.Combine(targetRoot, ".fm_staging", job.ToString("N"), "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "PRIOR");

        _journal.Append(Open(job, Path.Combine(_root, "src.dat"), final, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.KeepSource));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, Sha256 = Sha(content) });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = true });
        _journal.Append(new TargetVerifiedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new TargetStagedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, FinalPath = final, StagedPath = staged });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.CompletedForward);
        Assert.True(File.Exists(final));
        Assert.Equal(content, File.ReadAllText(final));   // completed forward
    }

    [Fact]
    public void Mid_placement_under_None_verification_rolls_back_and_restores_staged()
    {
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "output"), "new bytes");
        string targetRoot = Path.Combine(_root, "target");
        string final = Path.Combine(targetRoot, "out.dat");     // absent: the two-step fallback had moved it to staging
        string staged = Path.Combine(targetRoot, ".fm_staging", job.ToString("N"), "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "PRIOR VERSION");
        string temp = final + ".fmtmp-" + jobId.Short;
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(temp, "new bytes");

        _journal.Append(Open(job, Path.Combine(_root, "src.dat"), final, targetRoot, workspace, VerificationMethod.None, OnSuccessAction.KeepSource));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = Path.Combine(workspace, "output"), SizeBytes = 9, Sha256 = "" });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = true });
        _journal.Append(new TargetStagedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, FinalPath = final, StagedPath = staged });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.RolledBack);
        Assert.True(File.Exists(final));
        Assert.Equal("PRIOR VERSION", File.ReadAllText(final));   // conservative: prior restored
    }

    [Fact]
    public void Row_I_post_commit_reapplies_disposition()
    {
        var job = Guid.NewGuid();
        string source = Path.Combine(_root, "src.dat");
        File.WriteAllText(source, "delivered");
        string targetRoot = Path.Combine(_root, "target");
        string final = Path.Combine(targetRoot, "out.dat");

        _journal.Append(Open(job, source, final, targetRoot, Path.Combine(_root, "work", "w"), VerificationMethod.Sha256, OnSuccessAction.MoveToTrash));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = source, SizeBytes = 9, Sha256 = Sha("delivered") });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new JobCommittedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.CompletedForward);
        Assert.Contains(source, _trash.Trashed);       // disposition re-applied idempotently
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void A_closed_job_is_left_untouched()
    {
        var job = Guid.NewGuid();
        _journal.Append(Open(job, Path.Combine(_root, "s"), Path.Combine(_root, "f"), _root, _root, VerificationMethod.Sha256, OnSuccessAction.KeepSource));
        _journal.Append(new JobClosedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, Outcome = JobOutcome.Succeeded });

        Result<RecoveryReport, JobError> result = _recovery.Recover();
        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(0, report.JobsRecovered);
    }
}
