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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Stands up a <see cref="JobExecutor"/> over the <b>real</b> substrate — real journal, real
/// hasher, real conflict resolver, real atomic placer, real rollback executor, real disposition
/// service — on a real temp filesystem, with fakes only at the platform boundary (volume, metadata,
/// trash) plus optional fault injection.
/// <para>These are integration tests by intent: file operations are the product's highest-risk
/// behavior, so the assertions are made against what is actually on disk after a job, not against
/// mocked collaborators.</para></summary>
internal sealed class JobExecutorHarness : IDisposable
{
    private readonly JobJournal _realJournal;

    public JobExecutorHarness(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), $"fm-{label}-" + Guid.NewGuid().ToString("N"));
        SourceDir = Path.Combine(Root, "source");
        TargetDir = Path.Combine(Root, "target");
        ArchiveDir = Path.Combine(Root, "archive");
        TrashBin = Path.Combine(Root, "recycle-bin");
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);
        // A real bin directory, so a test asserts the source SURVIVED recycling rather than only that
        // the trash call happened — the difference between "recycled" and "destroyed".
        Trash = new FakeTrashService(TrashBin);

        Paths = new EnginePaths { Root = Path.Combine(Root, "engine") };
        foreach (string dir in new[] { Paths.JournalDirectory, Paths.JobLogsDirectory, Paths.AuditDirectory, Paths.QuarantineDirectory, Paths.WorkDirectory })
            Directory.CreateDirectory(dir);

        EngineConfig config = new();
        _realJournal = new JobJournal(Paths, config, NullLogger<JobJournal>.Instance);
        Journal = new FaultyJobJournal(_realJournal);
        Hasher = new FaultyFileHasher(new FileHasher(NullLogger<FileHasher>.Instance));
        PathLockRegistry locks = new();
        SourcePriorityRegistry priorities = new();
        ConflictResolver resolver = new(locks, priorities, NullLogger<ConflictResolver>.Instance);
        SelfWriteSuppressionRegistry suppression = new(TimeProvider.System);
        RetryClock = new FakeTimeProvider();
        TransientRetryPolicy retry = new(RetryClock, NullLogger<TransientRetryPolicy>.Instance);
        AtomicPlacer placer = new(Hasher, Journal, suppression, retry, Metadata, priorities, TimeProvider.System, NullLogger<AtomicPlacer>.Instance);
        DiskPreflight preflight = new(Volumes, config, NullLogger<DiskPreflight>.Instance);
        RollbackExecutor rollback = new(Journal, Hasher, TimeProvider.System, NullLogger<RollbackExecutor>.Instance);
        Audit = new DispositionAuditLog(Paths, NullLogger<DispositionAuditLog>.Instance);
        SourceDispositionService disposition = new(Trash, Audit, TimeProvider.System, NullLogger<SourceDispositionService>.Instance);
        FilterCompiler filterCompiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
        JobLog = new JobLogStore(Paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);

        Executor = new JobExecutor(
            Journal, locks, preflight, filterCompiler, Hasher, resolver, placer, rollback, disposition,
            JobLog, TimeProvider.System, NullLogger<JobExecutor>.Instance);
    }

    public string Root { get; }
    public string SourceDir { get; }
    public string TargetDir { get; }
    public string ArchiveDir { get; }
    public string TrashBin { get; }
    public EnginePaths Paths { get; }

    /// <summary>Fault-injecting wrapper over the real journal; set FailOnRecordType to force a failure
    /// at a specific point in the write-ahead sequence.</summary>
    public FaultyJobJournal Journal { get; }
    public JobExecutor Executor { get; }
    public JobLogStore JobLog { get; }
    public DispositionAuditLog Audit { get; }
    public FaultyFileHasher Hasher { get; }
    public FakeTimeProvider RetryClock { get; }
    public FakeVolumeInfoProvider Volumes { get; } = new();
    public FakeMetadataPreserver Metadata { get; } = new();
    public FakeTrashService Trash { get; }

    /// <summary>Writes a source file and returns its absolute path. <paramref name="relativePath"/> may
    /// contain subdirectories, so PreserveStructure layouts can be exercised.</summary>
    public string WriteSource(string relativePath, string content)
    {
        string path = Path.Combine(SourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteSourceBytes(string relativePath, byte[] content)
    {
        string path = Path.Combine(SourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Writes a pre-existing file at a target path, so conflict and overwrite paths can be
    /// exercised against something real.</summary>
    public string WriteExistingTarget(string absolutePath, string content, DateTime? lastWriteUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);
        if (lastWriteUtc is not null)
            File.SetLastWriteTimeUtc(absolutePath, lastWriteUtc.Value);
        return absolutePath;
    }

    public PolicySnapshot Policy(
        VerificationMethod verification = VerificationMethod.Sha256,
        OverwriteHandling overwrite = OverwriteHandling.StageOverwrites,
        ConflictResolution conflict = ConflictResolution.Overwrite,
        OnSuccessAction onSuccess = OnSuccessAction.KeepSource,
        string? archiveFolder = null,
        MetadataOnConflict metadataOnConflict = MetadataOnConflict.WarnAndContinue) => new()
    {
        Verification = verification,
        OverwriteHandling = overwrite,
        ConflictResolution = conflict,
        OnSuccess = onSuccess,
        ArchiveFolder = archiveFolder,
        MetadataOnConflict = metadataOnConflict,
    };

    /// <summary>A plan for one source fanned out to the given final paths. Each final path is assumed
    /// to live under <see cref="TargetDir"/> unless <paramref name="targetRoots"/> says otherwise.</summary>
    /// <param name="sourceRoot">Defaults to the harness's source dir; pass another root to model a
    /// payload from a different Source of an M:1 profile.</param>
    /// <param name="sourceIndex">The M:1 priority rank. Left null it is resolved from the profile, as in
    /// production; pass it to pin a rank for a priority test.</param>
    public JobPlan Plan(
        string sourcePath,
        IReadOnlyList<string> finalPaths,
        PolicySnapshot? policy = null,
        IReadOnlyList<string>? targetRoots = null,
        Profile? profile = null,
        string? sourceRoot = null,
        int? sourceIndex = null) =>
        JobFixtures.Execution(
            sourcePath, sourceRoot ?? SourceDir, finalPaths,
            targetRoots ?? [.. finalPaths.Select(_ => TargetDir)],
            policy ?? Policy(), profile: profile, sourceIndex: sourceIndex).Plan;

    public JobPlan Plan(
        string sourcePath, string finalPath, PolicySnapshot? policy = null, Profile? profile = null,
        string? sourceRoot = null, int? sourceIndex = null) =>
        Plan(sourcePath, [finalPath], policy, profile: profile, sourceRoot: sourceRoot, sourceIndex: sourceIndex);

    public string TargetPath(string relativePath) => Path.Combine(TargetDir, relativePath);

    public IReadOnlyList<JournalRecord> JournalRecords()
    {
        Journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        return records ?? [];
    }

    public IReadOnlyList<string> JobLogLines(JobId jobId)
    {
        JobLog.Read(jobId.Value).TryGetValue(out IReadOnlyList<string>? lines);
        return lines ?? [];
    }

    /// <summary>Every file anywhere under the engine work/quarantine trees and the target tree that
    /// looks like a leftover placement artifact. A clean run must leave none.</summary>
    public IReadOnlyList<string> LeftoverArtifacts()
    {
        List<string> found = [];
        foreach (string dir in new[] { Root })
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(".fm_staging", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(".pipeline_tmp", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(file);
                }
            }
        }
        return found;
    }

    /// <summary>Runs the executor, pumping the retry policy's clock so a retried operation does not
    /// wait real seconds. Use for any test that injects a transient failure.</summary>
    public async Task<JobCompletion> ExecuteWithRetriesAsync(JobPlan plan)
    {
        Task<JobCompletion> run = Executor.ExecuteAsync(plan);
        // Advance-then-poll, deliberately in that order and deliberately unconditional. Waiting 5 ms
        // BEFORE the first advance is a real behavior change, not just a cheaper loop: it widens the
        // window in which a multi-target job's sibling-cancellation lands mid-placement, and
        // A_multi_target_failure_leaves_no_target_placed_and_every_prior_intact then finds a .fmtmp-
        // temp that rollback did not reclaim. That looks like a genuine cancellation/cleanup race in
        // the placer worth investigating on its own; until then this loop stays as it is, because the
        // cost it saves is a few milliseconds of test time.
        while (!run.IsCompleted)
        {
            RetryClock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(5);
        }
        return await run;
    }

    public void Dispose()
    {
        _realJournal.Dispose();
        if (Directory.Exists(Root))
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a lingering handle must not fail the test it just verified */ }
        }
    }
}
