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
        var rollback = new RollbackExecutor(_journal, hasher, time, NullLogger<RollbackExecutor>.Instance);
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
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
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
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = Path.Combine(workspace, "output"), SizeBytes = 9, ContentHash = "" });
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
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = source, SizeBytes = 9, ContentHash = Sha("delivered") });
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

    // A two-target variant of Open(...) for the multi-target forward-completion test.
    private JobOpenedRecord Open2(Guid job, string sourcePath, string final0, string final1, string targetRoot, string workspace, VerificationMethod verification, OnSuccessAction onSuccess)
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
            Targets =
            [
                new TargetPlan { TargetIndex = 0, TargetRoot = targetRoot, ProspectiveFinalPath = final0 },
                new TargetPlan { TargetIndex = 1, TargetRoot = targetRoot, ProspectiveFinalPath = final1 },
            ],
        };

    [Fact]
    public void Lost_target_write_begin_refuses_forward_completion_and_preserves_the_source()
    {
        // OutputSealed (Sha256) is present and a staged record promotes the target to Staged, but the
        // target-write-begin was lost to a torn journal write — FinalPath/TempPath are null. The
        // forward gate must refuse (else it would commit + dispose over a silently skipped target).
        var job = Guid.NewGuid();
        string source = Path.Combine(_root, "src.dat");
        File.WriteAllText(source, "the irreplaceable source");
        const string content = "sealed payload";
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        string final = Path.Combine(targetRoot, "out.dat");
        string staged = Path.Combine(targetRoot, ".fm_staging", job.ToString("N"), "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "PRIOR");

        _journal.Append(Open(job, source, final, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.MoveToTrash));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
        // No TargetWriteBeginRecord — the lost record leaves FinalPath/TempPath null on the target.
        _journal.Append(new TargetStagedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, FinalPath = final, StagedPath = staged });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.RolledBack);
        Assert.Equal(0, report.CompletedForward);
        Assert.True(File.Exists(source));                 // source never disposed
        Assert.DoesNotContain(source, _trash.Trashed);    // and never trashed
    }

    [Fact]
    public void Placed_target_missing_after_crash_refuses_forward_completion()
    {
        // A Placed target whose final file is gone (mid-placement, JobCommitted NOT present) must not
        // complete forward — the source must never be disposed over a target we cannot honour.
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        string source = Path.Combine(_root, "src.dat");
        File.WriteAllText(source, "source");
        const string content = "sealed payload";
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        string final = Path.Combine(targetRoot, "out.dat");   // journal says Placed, but it is MISSING on disk
        string temp = final + ".fmtmp-" + jobId.Short;

        _journal.Append(Open(job, source, final, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.MoveToTrash));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = false });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        // No JobCommittedRecord — pre-commit, placed-but-missing.

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.RolledBack);
        Assert.Equal(0, report.CompletedForward);
        Assert.True(File.Exists(source));                 // not disposed over a missing placed target
        Assert.DoesNotContain(source, _trash.Trashed);
    }

    [Fact]
    public void Multi_target_all_placed_and_verified_completes_forward()
    {
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        const string content = "shared verified payload";
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        string final0 = Path.Combine(targetRoot, "out0.dat");
        string final1 = Path.Combine(targetRoot, "out1.dat");
        File.WriteAllText(final0, content);
        File.WriteAllText(final1, content);
        string temp0 = final0 + ".fmtmp-" + jobId.Short;
        string temp1 = final1 + ".fmtmp-" + jobId.Short;

        _journal.Append(Open2(job, Path.Combine(_root, "src.dat"), final0, final1, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.KeepSource));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp0, FinalPath = final0, FinalExisted = false });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 1, TempPath = temp1, FinalPath = final1, FinalExisted = false });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 1 });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.CompletedForward);
        Assert.Equal(0, report.RolledBack);
        Assert.True(File.Exists(final0));
        Assert.True(File.Exists(final1));
        Assert.Equal(content, File.ReadAllText(final0));
        Assert.Equal(content, File.ReadAllText(final1));
    }

    [Fact]
    public void Row_J_resumes_a_crashed_rollback()
    {
        // A rollback-begin is journalled but one target has no target-rolledback record: recovery
        // resumes the sweep for the un-reverted target.
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        string final = Path.Combine(targetRoot, "out.dat");
        string temp = final + ".fmtmp-" + jobId.Short;
        File.WriteAllText(temp, "unfinished temp");
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);

        _journal.Append(Open(job, Path.Combine(_root, "src.dat"), final, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.KeepSource));
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = false });
        _journal.Append(new TargetVerifiedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new RollbackBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, Reason = "verification mismatch", FailedTargetIndex = 0 });
        // No TargetRolledBackRecord — the rollback crashed before sweeping the target.

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.RolledBack);
        Assert.False(File.Exists(temp));            // resumed sweep removed the leftover temp
        Assert.False(Directory.Exists(workspace));  // and deleted the workspace
    }

    [Fact]
    public void External_file_at_unplaced_final_is_quarantined_not_overwritten()
    {
        // Two-target job: target 0 already Placed (so the job classifies as mid-placement and the
        // gate can pass), target 1 journaled FinalExisted=false and crashed before its rename. An
        // external program then created a file at target 1's final name. Forward completion must
        // quarantine that file — never File.Move(overwrite:true) over content the journal cannot
        // prove is ours.
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        const string content = "the verified payload";
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(targetRoot, "a"));
        Directory.CreateDirectory(Path.Combine(targetRoot, "b"));
        string final0 = Path.Combine(targetRoot, "a", "out.dat");
        string final1 = Path.Combine(targetRoot, "b", "out.dat");
        File.WriteAllText(final0, content);                        // target 0: placed and intact
        string temp1 = final1 + ".fmtmp-" + jobId.Short;
        File.WriteAllText(temp1, content);                         // target 1: verified temp awaiting rename
        File.WriteAllText(final1, "EXTERNAL FILE - someone else's data");

        _journal.Append(Open2(job, Path.Combine(_root, "src.dat"), final0, final1, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.KeepSource));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = final0 + ".fmtmp-" + jobId.Short, FinalPath = final0, FinalExisted = false });
        _journal.Append(new TargetVerifiedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 1, TempPath = temp1, FinalPath = final1, FinalExisted = false });
        _journal.Append(new TargetVerifiedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 1 });

        Result<RecoveryReport, JobError> result = _recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.CompletedForward);
        Assert.Equal(content, File.ReadAllText(final1));           // job output placed
        string quarantinePath = Assert.Single(report.QuarantinedPaths, p => p.Contains(job.ToString("N")));
        Assert.Equal("EXTERNAL FILE - someone else's data", File.ReadAllText(quarantinePath));   // foreign file preserved
        Assert.StartsWith(_paths.QuarantineDirectory, quarantinePath);
    }

    [Fact]
    public void Modified_placed_file_is_left_in_place_by_recovery_rollback()
    {
        // A fresh file was placed (state Placed journaled), the job crashed before commit, and the
        // user edited the placed file. The gate refuses forward (hash mismatch) — and the rollback
        // it falls through to must NOT delete the edited file (it no longer holds the job's bytes).
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        const string content = "the verified payload";
        string source = Path.Combine(_root, "src.dat");
        File.WriteAllText(source, "the irreplaceable source");
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        string final = Path.Combine(targetRoot, "out.dat");
        string temp = final + ".fmtmp-" + jobId.Short;
        File.WriteAllText(final, "USER EDITS after placement");   // placed, then modified externally

        _journal.Append(Open(job, source, final, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.MoveToTrash));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = false });
        _journal.Append(new TargetVerifiedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });

        // Post-recovery rotation drops CLOSED jobs' records, so observe the rollback's appends live.
        var spy = new RecordingJournal(_journal);
        CrashRecovery recovery = BuildRecovery(spy);

        Result<RecoveryReport, JobError> result = recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(1, report.RolledBack);
        Assert.Equal(0, report.CompletedForward);
        Assert.True(File.Exists(final));
        Assert.Equal("USER EDITS after placement", File.ReadAllText(final));   // never deleted
        Assert.True(File.Exists(source));                                      // source never disposed
        Assert.DoesNotContain(source, _trash.Trashed);
        Assert.Contains(spy.Appended, r => r is TargetRolledBackRecord { Action: RollbackAction.LeftInPlaceModified });
    }

    [Fact]
    public void Failed_commit_append_leaves_the_job_open_and_the_source_undisposed()
    {
        // job-committed is THE commit point: if its append fails, recovery must not dispose the
        // source — the job stays OPEN (copies intact) and the next startup retries. Target state
        // Placed makes the job mid-placement with a passing gate, so recovery reaches the commit.
        var job = Guid.NewGuid();
        var jobId = new JobId(job);
        const string content = "the verified payload";
        string source = Path.Combine(_root, "src.dat");
        File.WriteAllText(source, "the irreplaceable source");
        string workspace = Path.Combine(_root, "work", ".pipeline_tmp", job.ToString("N"));
        Directory.CreateDirectory(workspace);
        string output = Path.Combine(workspace, "output");
        File.WriteAllText(output, content);
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetRoot);
        string final = Path.Combine(targetRoot, "out.dat");
        string temp = final + ".fmtmp-" + jobId.Short;
        File.WriteAllText(final, content);                        // placed and intact

        _journal.Append(Open(job, source, final, targetRoot, workspace, VerificationMethod.Sha256, OnSuccessAction.MoveToTrash));
        _journal.Append(new OutputSealedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, OutputPath = output, SizeBytes = content.Length, ContentHash = Sha(content) });
        _journal.Append(new TargetWriteBeginRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0, TempPath = temp, FinalPath = final, FinalExisted = false });
        _journal.Append(new TargetVerifiedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });
        _journal.Append(new TargetPlacedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, TargetIndex = 0 });

        CrashRecovery recovery = BuildRecovery(new CommitAppendFailingJournal(_journal));

        Result<RecoveryReport, JobError> result = recovery.Recover();

        Assert.True(result.TryGetValue(out RecoveryReport? report));
        Assert.Equal(0, report.JobsRecovered);                    // left for the next startup
        Assert.True(File.Exists(source));                         // source never disposed
        Assert.DoesNotContain(source, _trash.Trashed);
        Assert.Equal(content, File.ReadAllText(final));           // placed copy intact
        Assert.True(_journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records));
        Assert.DoesNotContain(records!, r => r is JobCommittedRecord);   // commit never persisted
        Assert.DoesNotContain(records!, r => r is JobClosedRecord);      // job still OPEN
    }

    /// <summary>A recovery built over <paramref name="journal"/> with the same collaborators as the
    /// fixture's default instance.</summary>
    private CrashRecovery BuildRecovery(IJobJournal journal)
    {
        var time = new FakeTimeProvider();
        var hasher = new FileHasher(NullLogger<FileHasher>.Instance);
        var rollback = new RollbackExecutor(journal, hasher, time, NullLogger<RollbackExecutor>.Instance);
        var disposition = new SourceDispositionService(_trash, new DispositionAuditLog(_paths, NullLogger<DispositionAuditLog>.Instance), time, NullLogger<SourceDispositionService>.Instance);
        return new CrashRecovery(journal, hasher, rollback, disposition, _paths,
            new EngineConfig { TempRoot = Path.Combine(_root, "work") }, time, NullLogger<CrashRecovery>.Instance);
    }

    /// <summary>Delegates to the real journal but fails every <see cref="JobCommittedRecord"/>
    /// append — the disk-full-at-the-commit-point fault.</summary>
    private sealed class CommitAppendFailingJournal(IJobJournal inner) : IJobJournal
    {
        public Result Append(JournalRecord record) =>
            record is JobCommittedRecord ? Result.Failure("injected: commit append failed") : inner.Append(record);
        public Result<IReadOnlyList<JournalRecord>, JobError> ReadAll() => inner.ReadAll();
        public Result Rotate() => inner.Rotate();
    }

    /// <summary>Delegates to the real journal and records every append — post-recovery rotation
    /// drops CLOSED jobs' records, so assertions about rollback rows must observe them live.</summary>
    private sealed class RecordingJournal(IJobJournal inner) : IJobJournal
    {
        public List<JournalRecord> Appended { get; } = [];
        public Result Append(JournalRecord record) { lock (Appended) Appended.Add(record); return inner.Append(record); }
        public Result<IReadOnlyList<JournalRecord>, JobError> ReadAll() => inner.ReadAll();
        public Result Rotate() => inner.Rotate();
    }
}
