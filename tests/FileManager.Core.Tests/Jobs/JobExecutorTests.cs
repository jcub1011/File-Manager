using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
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
    private readonly EnginePaths _paths;
    private readonly EngineConfig _config = new();

    public JobExecutorTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        _targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);

        _paths = new EnginePaths { Root = Path.Combine(_root, "engine") };
        foreach (string dir in new[] { _paths.JournalDirectory, _paths.JobLogsDirectory, _paths.AuditDirectory })
            Directory.CreateDirectory(dir);

        _journal = new JobJournal(_paths, _config, NullLogger<JobJournal>.Instance);
        _executor = CreateExecutor(new FileHasher(NullLogger<FileHasher>.Instance));
    }

    /// <summary>The full live substrate, parameterised only by the hasher so a test can observe or perturb
    /// hashing. The SAME hasher instance goes to the executor and the placer, which is what lets a
    /// counting decorator see the total read cost of a job.</summary>
    private JobExecutor CreateExecutor(IFileHasher hasher)
    {
        EnginePaths paths = _paths;
        EngineConfig config = _config;
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

        return new JobExecutor(_journal, locks, preflight, filterCompiler, hasher, resolver, placer, rollback, disposition, jobLog, TimeProvider.System, NullLogger<JobExecutor>.Instance);
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

    /// <summary>Collects the raw samples the executor pushes — no throttling, no clamping (that is
    /// <see cref="JobProgressPublisher"/>'s job), so these assert what the phase algorithm reports.</summary>
    private sealed class RecordingProgress : IProgress<JobProgress>
    {
        private readonly List<JobProgress> _samples = [];
        public IReadOnlyList<JobProgress> Samples
        {
            get { lock (_samples) return _samples.ToList(); }
        }
        public void Report(JobProgress value)
        {
            lock (_samples) _samples.Add(value);
        }
    }

    [Fact]
    public async Task Reports_progress_through_the_phases_of_a_successful_job()
    {
        string source = WriteSource("progress.txt", "payload");
        RecordingProgress progress = new();

        JobCompletion completion = await _executor.ExecuteAsync(
            Plan(source, Path.Combine(_targetDir, "progress.txt"), _targetDir, Policy()), progress);

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        IReadOnlyList<JobPhase> phases = progress.Samples.Select(s => s.Phase).ToList();
        Assert.Equal(
            [JobPhase.Locking, JobPhase.Opening, JobPhase.Preflighting, JobPhase.Screening,
             JobPhase.Sealing, JobPhase.Distributing],
            phases.Take(6));
        Assert.Equal(JobPhase.Disposing, phases[^1]);
        Assert.Contains(JobPhase.Committing, phases);
        // The last distribute sample accounts for every target.
        JobProgress lastDistribute = progress.Samples.Last(s => s.Phase == JobPhase.Distributing);
        Assert.Equal(lastDistribute.TargetCount, lastDistribute.TargetsCompleted);
    }

    [Fact]
    public async Task Reports_targets_completed_for_a_multi_target_job()
    {
        string source = WriteSource("fanout.txt", "payload");
        string targetB = Path.Combine(_root, "target-b");
        Directory.CreateDirectory(targetB);
        JobPlan plan = JobFixtures.Execution(
            source, _sourceDir,
            [Path.Combine(_targetDir, "fanout.txt"), Path.Combine(targetB, "fanout.txt")],
            [_targetDir, targetB], Policy()).Plan;
        RecordingProgress progress = new();

        JobCompletion completion = await _executor.ExecuteAsync(plan, progress);

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        IReadOnlyList<int> distributeCounts = progress.Samples
            .Where(s => s.Phase == JobPhase.Distributing)
            .Select(s => s.TargetsCompleted).ToList();
        Assert.All(progress.Samples.Where(s => s.Phase == JobPhase.Distributing),
            s => Assert.Equal(2, s.TargetCount));
        // Targets settle in parallel, so ordering is unspecified — but every target must be counted.
        Assert.Equal(2, distributeCounts.Max());
        Assert.Contains(0, distributeCounts);   // the phase-entry sample
    }

    [Fact]
    public async Task Reports_rolling_back_when_a_placement_fails()
    {
        string source = WriteSource("rollback-progress.txt", "payload");
        _metadata.FailApply = true;
        RecordingProgress progress = new();

        JobCompletion completion = await _executor.ExecuteAsync(
            Plan(source, Path.Combine(_targetDir, "rollback-progress.txt"), _targetDir,
                Policy(metadataOnConflict: MetadataOnConflict.FailJob)), progress);

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.Contains(JobPhase.RollingBack, progress.Samples.Select(s => s.Phase));
    }

    [Fact]
    public async Task A_throwing_progress_sink_does_not_change_the_outcome()
    {
        string source = WriteSource("hostile-sink.txt", "payload");
        string final = Path.Combine(_targetDir, "hostile-sink.txt");

        JobCompletion completion = await _executor.ExecuteAsync(
            Plan(source, final, _targetDir, Policy()), new ThrowingProgress());

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.True(File.Exists(final));
    }

    private sealed class ThrowingProgress : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => throw new InvalidOperationException("hostile sink");
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
    public async Task An_unchanged_file_is_never_sealed_so_its_full_read_is_never_paid()
    {
        // The §3.4.1 identity probe runs BEFORE sealing. The absence of an output-sealed record is the
        // observable proof: that record is written immediately after SealSourceAsync hashes the source end
        // to end, so no record means no full source read happened.
        string source = WriteSource("same.txt", "identical");
        string final = Path.Combine(_targetDir, "same.txt");
        File.WriteAllText(final, "identical");

        JobCompletion completion = await _executor.ExecuteAsync(Plan(source, final, _targetDir, Policy()));

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.UnchangedAtAllTargets, completion.SkipReason);
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.DoesNotContain(records!, r => r is OutputSealedRecord);
        Assert.Contains(records!, r => r is TargetUnchangedRecord);
    }

    [Fact]
    public async Task A_placed_file_is_still_sealed_exactly_once()
    {
        // The mirror of the test above: when a write IS needed the reference hash must still exist, and the
        // probe's hash is handed to the seal rather than the source being read a second time.
        string source = WriteSource("fresh2.txt", "content");
        string final = Path.Combine(_targetDir, "fresh2.txt");

        JobCompletion completion = await _executor.ExecuteAsync(Plan(source, final, _targetDir, Policy()));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        OutputSealedRecord sealed_ = Assert.Single(records!.OfType<OutputSealedRecord>());
        Assert.False(string.IsNullOrEmpty(sealed_.ContentHash));
    }

    [Fact]
    public async Task A_sampled_identity_settles_a_large_duplicate_from_a_bounded_read()
    {
        // The headline claim, measured. The file is comfortably larger than the sampled budget, and the
        // destination is byte-identical, so today's behaviour would have read it twice in full.
        long length = SampledHashLayout.Default.BudgetBytes * 4;      // 32 MiB
        string source = WriteLargeSource("movie.bin", length);
        string final = Path.Combine(_targetDir, "movie.bin");
        File.Copy(source, final);

        CountingFileHasher counting = new(new FileHasher(NullLogger<FileHasher>.Instance));
        JobExecutor executor = CreateExecutor(counting);

        JobCompletion completion = await executor.ExecuteAsync(
            Plan(source, final, _targetDir, Policy() with
            {
                LargeFileIdentity = LargeFileIdentity.SampledHash,
                LargeFileIdentityThresholdBytes = SampledHashLayout.Default.BudgetBytes,
            }));

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.UnchangedAtAllTargets, completion.SkipReason);

        // Two sampled reads (source + destination) and not one full read of either.
        Assert.Equal(2, counting.SampledHashes);
        Assert.Equal(0, counting.FullHashes);
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.DoesNotContain(records!, r => r is OutputSealedRecord);

        // And the saving is real: a full-hash run over the same pair reads both files end to end.
        CountingFileHasher exact = new(new FileHasher(NullLogger<FileHasher>.Instance));
        await CreateExecutor(exact).ExecuteAsync(Plan(source, final, _targetDir, Policy()));
        Assert.Equal(2, exact.FullHashes);
        Assert.Equal(0, exact.SampledHashes);
    }

    [Fact]
    public async Task A_sampled_identity_still_copies_a_same_size_file_whose_content_differs()
    {
        // Sampling is EXACT about "these differ". A destination of identical length but different bytes
        // must still be overwritten — otherwise the option would be silently lossy rather than merely
        // probabilistic.
        long length = SampledHashLayout.Default.BudgetBytes * 4;
        string source = WriteLargeSource("differs.bin", length, seed: 1);
        string final = Path.Combine(_targetDir, "differs.bin");
        WriteLargeFile(final, length, seed: 2);

        JobCompletion completion = await _executor.ExecuteAsync(
            Plan(source, final, _targetDir, Policy() with
            {
                LargeFileIdentity = LargeFileIdentity.SampledHash,
                LargeFileIdentityThresholdBytes = SampledHashLayout.Default.BudgetBytes,
            }));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(final));

        // Integrity is untouched: the written copy is still sealed and verified against a FULL hash.
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.Contains(records!, r => r is OutputSealedRecord);
        Assert.Contains(records!, r => r is TargetVerifiedRecord);
    }

    [Fact]
    public async Task A_fresh_copy_pays_no_identity_read_at_all()
    {
        // No destination file means nothing to compare against, so the probe must not hash the incoming
        // file for identity — only the seal (one full read) and the read-back verify should occur.
        string source = WriteLargeSource("brand-new.bin", SampledHashLayout.Default.BudgetBytes * 2);
        CountingFileHasher counting = new(new FileHasher(NullLogger<FileHasher>.Instance));

        JobCompletion completion = await CreateExecutor(counting).ExecuteAsync(
            Plan(source, Path.Combine(_targetDir, "brand-new.bin"), _targetDir, Policy() with
            {
                LargeFileIdentity = LargeFileIdentity.SampledHash,
                LargeFileIdentityThresholdBytes = SampledHashLayout.Default.BudgetBytes,
            }));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal(0, counting.SampledHashes);
    }

    private string WriteLargeSource(string name, long length, int seed = 7)
    {
        string path = Path.Combine(_sourceDir, name);
        WriteLargeFile(path, length, seed);
        return path;
    }

    private static void WriteLargeFile(string path, long length, int seed = 7)
    {
        byte[] block = new byte[64 * 1024];
        Random rng = new(seed);
        using FileStream fs = File.Create(path);
        for (long written = 0; written < length; written += block.Length)
        {
            rng.NextBytes(block);
            fs.Write(block, 0, (int)Math.Min(block.Length, length - written));
        }
    }

    /// <summary>Counts hash calls by kind, so a test can assert how a job actually paid for identity rather
    /// than inferring it from the outcome.</summary>
    private sealed class CountingFileHasher(IFileHasher inner) : IFileHasher
    {
        private int _full;
        private int _sampled;

        public int FullHashes => Volatile.Read(ref _full);
        public int SampledHashes => Volatile.Read(ref _sampled);

        public Task<Result<string, JobError>> HashFileAsync(string path, VerificationMethod method, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _full);
            return inner.HashFileAsync(path, method, ct);
        }

        public Task<Result<byte[], JobError>> HashFileToBytesAsync(string path, VerificationMethod method, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _full);
            return inner.HashFileToBytesAsync(path, method, ct);
        }

        public Task<Result<byte[], JobError>> HashSampledToBytesAsync(string path, SampledHashLayout layout, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _sampled);
            return inner.HashSampledToBytesAsync(path, layout, ct);
        }
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
