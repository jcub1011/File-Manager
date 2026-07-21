using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Journal;

public sealed class RollbackExecutorTests : IDisposable
{
    private readonly string _root;
    private readonly JobJournal _journal;
    private readonly RollbackExecutor _rollback;

    public RollbackExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var paths = new EnginePaths { Root = Path.Combine(_root, "engine") };
        _journal = new JobJournal(paths, new EngineConfig(), NullLogger<JobJournal>.Instance);
        _rollback = new RollbackExecutor(_journal, new FileHasher(NullLogger<FileHasher>.Instance), new FakeTimeProvider(), NullLogger<RollbackExecutor>.Instance);
    }

    public void Dispose()
    {
        _journal.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private RollbackContext Context(OverwriteHandling overwrite, params TargetRollbackItem[] targets) => new()
    {
        JobId = JobId.New(),
        Cause = new JobError { Code = JobErrorCode.VerificationMismatch, Message = "test" },
        Targets = targets,
        WorkspaceDir = Path.Combine(_root, "workspace"),
        OverwriteHandling = overwrite,
    };

    [Fact]
    public void Verified_temp_is_removed()
    {
        string temp = Path.Combine(_root, "a.fmtmp");
        File.WriteAllText(temp, "temp");
        var result = _rollback.Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Verified, TempPath = temp, FinalPath = Path.Combine(_root, "a"), StagedPath = null, FinalExistedBeforeJob = false,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.False(File.Exists(temp));
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.RemovedTemp });
    }

    [Fact]
    public void Placed_fresh_file_is_deleted()
    {
        string final = Path.Combine(_root, "fresh.txt");
        File.WriteAllText(final, "new");
        var result = _rollback.Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = null, FinalExistedBeforeJob = false,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.False(File.Exists(final));
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.UnplacedNoPrior });
    }

    [Fact]
    public void Placed_over_a_staged_prior_restores_the_prior_version()
    {
        string final = Path.Combine(_root, "doc.txt");
        string staged = Path.Combine(_root, "staging", "doc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(final, "NEW (bad)");
        File.WriteAllText(staged, "PRIOR (good)");

        var result = _rollback.Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = staged, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.Equal("PRIOR (good)", File.ReadAllText(final));
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.UnplacedAndRestored });
    }

    [Fact]
    public void DirectOverwrite_of_a_prior_leaves_the_placed_file_unrecoverable()
    {
        string final = Path.Combine(_root, "doc.txt");
        File.WriteAllText(final, "NEW (kept)");
        var result = _rollback.Rollback(Context(OverwriteHandling.DirectOverwrite, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = null, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.Equal("NEW (kept)", File.ReadAllText(final));   // not deleted — prior is unrecoverable
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.LeftInPlaceUnrecoverable });
    }

    [Fact]
    public void Unchanged_target_is_never_reverted()
    {
        string final = Path.Combine(_root, "kept.txt");
        File.WriteAllText(final, "content that predates the job");
        _rollback.Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.SatisfiedUnchanged, TempPath = null, FinalPath = final, StagedPath = null, FinalExistedBeforeJob = true,
        }));

        Assert.True(File.Exists(final));
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.None });
    }

    [Fact]
    public void Closes_the_job_and_deletes_the_workspace()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "artifact"), "x");

        var result = _rollback.Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Pending, TempPath = null, FinalPath = null, StagedPath = null, FinalExistedBeforeJob = false,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.False(Directory.Exists(workspace));
        Assert.Contains(ReadJournal(), x => x is RollbackBeginRecord);
        Assert.Contains(ReadJournal(), x => x is JobClosedRecord { Outcome: JobOutcome.Failed });
    }

    // A rollback executor with the inter-attempt backoff disabled so retry paths don't Thread.Sleep.
    private RollbackExecutor ZeroDelay() =>
        new(_journal, new FileHasher(NullLogger<FileHasher>.Instance), new FakeTimeProvider(), NullLogger<RollbackExecutor>.Instance) { RetryDelay = TimeSpan.Zero };

    [Fact]
    public void Staged_two_step_restore_restores_the_prior_file()
    {
        // Staged: the prior was moved out to staging, the rename to final never happened (final absent).
        // Rollback moves staging -> final and drops the temp.
        string final = Path.Combine(_root, "doc.txt");   // absent
        string staged = Path.Combine(_root, "staging", "doc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "PRIOR (good)");
        string temp = Path.Combine(_root, "doc.fmtmp");
        File.WriteAllText(temp, "new bytes");

        var result = ZeroDelay().Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Staged, TempPath = temp, FinalPath = final, StagedPath = staged, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.True(File.Exists(final));
        Assert.Equal("PRIOR (good)", File.ReadAllText(final));   // prior restored
        Assert.False(File.Exists(temp));                          // temp removed
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.RestoredStagedBeforePlacement });
    }

    [Fact]
    public void A_residual_yields_incomplete_and_a_rollback_failed_close()
    {
        // Placed + StageOverwrites + a prior existed, but the staged path is missing: the restore
        // cannot run, so this target becomes a residual and the job closes RollbackFailed.
        string final = Path.Combine(_root, "doc.txt");
        File.WriteAllText(final, "NEW (bad)");

        var result = ZeroDelay().Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = null, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r));
        Assert.False(r.Complete);
        Assert.Contains(final, r.ResidualPaths);
        Assert.Contains(ReadJournal(), x => x is JobClosedRecord { Outcome: JobOutcome.RollbackFailed });
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Error: not null });
    }

    [Fact]
    public void Staging_dir_with_an_unrestored_file_is_kept()
    {
        // I-STAGING-KEEP: the restore fails (final is a directory, so replace/move cannot land the
        // staged file), leaving an unrestored file in the staging dir — which must NOT be deleted.
        string stagingDir = Path.Combine(_root, "staging", "job");
        Directory.CreateDirectory(stagingDir);
        string staged = Path.Combine(stagingDir, "doc.txt");
        File.WriteAllText(staged, "PRIOR (unrestored)");
        string final = Path.Combine(_root, "doc.txt");
        Directory.CreateDirectory(final);   // final is a directory => the restore I/O fails

        var result = ZeroDelay().Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = staged, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r));
        Assert.False(r.Complete);
        Assert.True(Directory.Exists(stagingDir));   // kept: it still holds an unrestored file
        Assert.True(File.Exists(staged));            // the unrestored file survives for later quarantine
    }

    [Fact]
    public void Staged_record_with_replace_never_run_deletes_temp_only()
    {
        // The placer journals target-staged BEFORE the replace executes; if the replace never ran,
        // the staged file is absent and the prior sits untouched at the final name. Rollback must
        // recognize "nothing moved yet" — delete the temp, touch nothing else, report no residual.
        string final = Path.Combine(_root, "doc.txt");
        File.WriteAllText(final, "PRIOR (untouched)");
        string staged = Path.Combine(_root, "staging", "doc.txt");   // never created
        string temp = Path.Combine(_root, "doc.fmtmp");
        File.WriteAllText(temp, "new bytes");

        var result = ZeroDelay().Rollback(Context(OverwriteHandling.StageOverwrites, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Staged, TempPath = temp, FinalPath = final, StagedPath = staged, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);   // no false RollbackFailed
        Assert.Equal("PRIOR (untouched)", File.ReadAllText(final));
        Assert.False(File.Exists(temp));                                        // temp not leaked
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.RemovedTemp, Error: null });
    }

    [Fact]
    public void Staged_with_completed_replace_restores_the_prior_when_the_final_is_ours()
    {
        // File.Replace fully happened (staged holds the prior, final holds the job's output) but the
        // crash lost target-placed. With the hash gate confirming the final is the job's own bytes,
        // rollback restores the prior instead of reporting a residual.
        const string newContent = "job output bytes";
        string final = Path.Combine(_root, "doc.txt");
        File.WriteAllText(final, newContent);
        string staged = Path.Combine(_root, "staging", "doc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "PRIOR (good)");

        var result = ZeroDelay().Rollback(HashGatedContext(newContent, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Staged, TempPath = null, FinalPath = final, StagedPath = staged, FinalExistedBeforeJob = true,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.Equal("PRIOR (good)", File.ReadAllText(final));   // prior restored
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.UnplacedAndRestored });
    }

    [Fact]
    public void Placed_final_modified_externally_is_left_in_place()
    {
        // The hash gate: a placed fresh file that no longer matches the job's output hash was
        // modified after placement — rollback must leave it, record a residual, and close RollbackFailed.
        string final = Path.Combine(_root, "fresh.txt");
        File.WriteAllText(final, "USER EDITS after placement");

        var result = ZeroDelay().Rollback(HashGatedContext("the job's original output", new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = null, FinalExistedBeforeJob = false,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r));
        Assert.False(r.Complete);
        Assert.Contains(final, r.ResidualPaths);
        Assert.Equal("USER EDITS after placement", File.ReadAllText(final));   // never deleted
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.LeftInPlaceModified });
    }

    [Fact]
    public void Placed_final_matching_the_job_output_is_still_deleted()
    {
        // The gate must not change the normal revert: a fresh placed file that still holds the
        // job's own bytes is deleted as before.
        const string content = "the job's output";
        string final = Path.Combine(_root, "fresh.txt");
        File.WriteAllText(final, content);

        var result = ZeroDelay().Rollback(HashGatedContext(content, new TargetRollbackItem
        {
            TargetIndex = 0, State = TargetState.Placed, TempPath = null, FinalPath = final, StagedPath = null, FinalExistedBeforeJob = false,
        }));

        Assert.True(result.TryGetValue(out RollbackResult? r) && r.Complete);
        Assert.False(File.Exists(final));
        Assert.Contains(ReadJournal(), x => x is TargetRolledBackRecord { Action: RollbackAction.UnplacedNoPrior });
    }

    /// <summary>A context whose hash gate is armed with the SHA-256 of <paramref name="jobOutputContent"/>.</summary>
    private RollbackContext HashGatedContext(string jobOutputContent, params TargetRollbackItem[] targets) =>
        Context(OverwriteHandling.StageOverwrites, targets) with
        {
            ExpectedContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(jobOutputContent))),
            Verification = VerificationMethod.Sha256,
        };

    private IReadOnlyList<JournalRecord> ReadJournal()
    {
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        return records!;
    }
}
