using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Placement;
using FileManager.Core.Preflight;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Drives the live executor over the real substrate (journal, placer, rollback,
/// disposition) with fakes only at the platform boundary (volume/metadata/trash).</summary>
public sealed class JobExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-exec-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private readonly JobJournal _journal;
    private readonly FakeMetadataPreserver _metadata = new();
    private readonly JobExecutor _executor;

    public JobExecutorTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        _targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);

        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        foreach (string dir in new[] { paths.JournalDirectory, paths.JobLogsDirectory, paths.AuditDirectory })
            Directory.CreateDirectory(dir);
        EngineConfig config = new();

        _journal = new JobJournal(paths, config, NullLogger<JobJournal>.Instance);
        FileHasher hasher = new(NullLogger<FileHasher>.Instance);
        PathLockRegistry locks = new();
        SourcePriorityRegistry priorities = new();
        ConflictResolver resolver = new(locks, priorities, NullLogger<ConflictResolver>.Instance);
        SelfWriteSuppressionRegistry suppression = new(TimeProvider.System);
        TransientRetryPolicy retry = new(TimeProvider.System, NullLogger<TransientRetryPolicy>.Instance);
        AtomicPlacer placer = new(hasher, _journal, suppression, retry, _metadata, priorities, TimeProvider.System, NullLogger<AtomicPlacer>.Instance);
        DiskPreflight preflight = new(new FakeVolumeInfoProvider(), config, NullLogger<DiskPreflight>.Instance);
        RollbackExecutor rollback = new(_journal, hasher, TimeProvider.System, NullLogger<RollbackExecutor>.Instance);
        DispositionAuditLog audit = new(paths, NullLogger<DispositionAuditLog>.Instance);
        SourceDispositionService disposition = new(new FakeTrashService(), audit, TimeProvider.System, NullLogger<SourceDispositionService>.Instance);
        FilterCompiler filterCompiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
        JobLogStore jobLog = new(paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);

        _executor = new JobExecutor(_journal, locks, preflight, filterCompiler, hasher, resolver, placer, rollback, disposition, jobLog, TimeProvider.System, NullLogger<JobExecutor>.Instance);
    }

    private PolicySnapshot Policy(
        ConflictResolution conflict = ConflictResolution.Overwrite,
        OnSuccessAction onSuccess = OnSuccessAction.KeepSource,
        MetadataOnConflict metadataOnConflict = MetadataOnConflict.WarnAndContinue) => new()
    {
        Verification = VerificationMethod.Sha256,
        OverwriteHandling = OverwriteHandling.StageOverwrites,
        ConflictResolution = conflict,
        OnSuccess = onSuccess,
        ArchiveFolder = null,
        MetadataOnConflict = metadataOnConflict,
    };

    private JobPlan Plan(string sourceFile, string finalPath, string targetRoot, PolicySnapshot policy) =>
        JobFixtures.Execution(sourceFile, _sourceDir, [finalPath], [targetRoot], policy).Plan;

    private string WriteSource(string name, string content)
    {
        string path = Path.Combine(_sourceDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Places_a_fresh_file_keeps_the_source_and_journals_commit_then_close()
    {
        string source = WriteSource("fresh.txt", "hello world");
        string final = Path.Combine(_targetDir, "fresh.txt");

        JobCompletion completion = await _executor.ExecuteAsync(Plan(source, final, _targetDir, Policy()));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.True(File.Exists(final));
        Assert.Equal("hello world", File.ReadAllText(final));
        Assert.True(File.Exists(source));   // OnSuccess = KeepSource

        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.Contains(records!, r => r is JobCommittedRecord);
        Assert.Contains(records!, r => r is JobClosedRecord { Outcome: JobOutcome.Succeeded });
    }

    [Fact]
    public async Task Re_running_an_identical_file_skips_unchanged_without_committing()
    {
        string source = WriteSource("same.txt", "identical");
        string final = Path.Combine(_targetDir, "same.txt");
        File.WriteAllText(final, "identical");   // target already holds the same bytes

        JobCompletion completion = await _executor.ExecuteAsync(Plan(source, final, _targetDir, Policy()));

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.UnchangedAtAllTargets, completion.SkipReason);

        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.DoesNotContain(records!, r => r is JobCommittedRecord);
        Assert.Contains(records!, r => r is TargetUnchangedRecord);
    }

    [Fact]
    public async Task Conflict_skip_keeps_the_existing_target_and_succeeds()
    {
        string source = WriteSource("keep.txt", "incoming");
        string final = Path.Combine(_targetDir, "keep.txt");
        File.WriteAllText(final, "the original");   // different content, will be kept

        JobCompletion completion = await _executor.ExecuteAsync(Plan(source, final, _targetDir, Policy(conflict: ConflictResolution.Skip)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("the original", File.ReadAllText(final));   // untouched
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.Contains(records!, r => r is TargetSkippedRecord);
    }

    [Fact]
    public async Task A_target_resolving_to_the_source_fails_with_self_path()
    {
        string source = WriteSource("self.txt", "x");
        // Target final path == the source path.
        JobCompletion completion = await _executor.ExecuteAsync(Plan(source, source, _sourceDir, Policy()));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.SelfPathTarget, completion.Error!.Code);
    }

    [Fact]
    public async Task A_transformer_profile_is_refused_without_opening_a_job()
    {
        string source = WriteSource("t.txt", "x");
        string final = Path.Combine(_targetDir, "t.txt");
        JobPlan plan = Plan(source, final, _targetDir, Policy());
        plan = plan with
        {
            Profile = plan.Profile with
            {
                Transformers =
                [
                    new TransformerStep
                    {
                        Step = 1, Name = "noop", ExecutablePath = @"C:\noop.exe",
                        ArgumentMode = ArgumentMode.Literal, Arguments = "", OutputMode = OutputMode.InPlace, TimeoutSeconds = 5,
                    },
                ],
            },
        };

        JobCompletion completion = await _executor.ExecuteAsync(plan);

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.TransformerFailed, completion.Error!.Code);
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.Empty(records!);   // refused before Open — nothing durable
    }

    [Fact]
    public async Task A_source_gone_under_the_lock_skips_with_no_journal_record()
    {
        string source = WriteSource("vanish.txt", "x");
        string final = Path.Combine(_targetDir, "vanish.txt");
        JobPlan plan = Plan(source, final, _targetDir, Policy());
        File.Delete(source);   // gone between plan build and execution

        JobCompletion completion = await _executor.ExecuteAsync(plan);

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.SourceDisposed, completion.SkipReason);
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.Empty(records!);
    }

    [Fact]
    public async Task A_placement_failure_rolls_back_leaving_the_source_and_no_target()
    {
        string source = WriteSource("boom.txt", "payload");
        string final = Path.Combine(_targetDir, "boom.txt");
        _metadata.FailApply = true;   // FailJob turns the metadata-apply loss into a placement failure

        JobCompletion completion = await _executor.ExecuteAsync(
            Plan(source, final, _targetDir, Policy(metadataOnConflict: MetadataOnConflict.FailJob)));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.False(File.Exists(final), "a failed placement must leave no target file");
        Assert.True(File.Exists(source), "rollback never touches the source (I-SOURCE-RB)");
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.Contains(records!, r => r is RollbackBeginRecord);
        Assert.Contains(records!, r => r is JobClosedRecord { Outcome: JobOutcome.Failed });
    }

    public void Dispose()
    {
        _journal.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
