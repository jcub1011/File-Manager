using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
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
        _rollback = new RollbackExecutor(_journal, new FakeTimeProvider(), NullLogger<RollbackExecutor>.Instance);
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

    private IReadOnlyList<JournalRecord> ReadJournal()
    {
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        return records!;
    }
}
