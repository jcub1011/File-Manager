using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Placement;
using FileManager.Core.Preflight;
using FileManager.Core.Scanning;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Preview/run parity for the §3.4.1 identity decision, end to end over the real substrate.
///
/// <para>This is the guard that matters most about <see cref="LargeFileIdentity"/>: the preview is what the
/// user approves, so if it promises "skip, already there" the executor must skip, and if it promises a write
/// the executor must write. Both sides derive their verdict from <see cref="IdentityStrategy"/>; these tests
/// fail if either one grows a special case the other does not have.</para></summary>
public sealed class IdentityParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-parity-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _targetDir;
    private readonly EnginePaths _paths;
    private readonly EngineConfig _config = new();
    private readonly JobJournal _journal;

    public IdentityParityTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        _targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);

        _paths = new EnginePaths { Root = Path.Combine(_root, "engine") };
        foreach (string dir in new[] { _paths.JournalDirectory, _paths.JobLogsDirectory, _paths.AuditDirectory })
            Directory.CreateDirectory(dir);
        _journal = new JobJournal(_paths, _config, NullLogger<JobJournal>.Instance);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Every (policy × duplicate-or-not) combination that the identity axis can produce. The
    /// threshold is 0 so the small fixtures still take the large-file path.</summary>
    public static TheoryData<LargeFileIdentity, bool> Matrix()
    {
        TheoryData<LargeFileIdentity, bool> data = [];
        foreach (LargeFileIdentity identity in Enum.GetValues<LargeFileIdentity>())
        {
            data.Add(identity, true);
            data.Add(identity, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_preview_and_the_run_reach_the_same_verdict(LargeFileIdentity identity, bool duplicate)
    {
        const string name = "clip.bin";
        string source = Path.Combine(_sourceDir, name);
        string final = Path.Combine(_targetDir, name);
        File.WriteAllText(source, "the incoming content");

        if (duplicate)
        {
            // Byte-identical AND same mtime, so every policy — content-based or metadata-based — should
            // agree it is already there.
            File.Copy(source, final);
            File.SetLastWriteTimeUtc(final, File.GetLastWriteTimeUtc(source));
        }
        else
        {
            // Same length, different bytes, and a clearly different mtime, so no policy should call it
            // unchanged: the content check sees different bytes, the metadata check sees a different time.
            File.WriteAllText(final, "the OTHER content!!!");
            Assert.Equal(new FileInfo(source).Length, new FileInfo(final).Length);
            File.SetLastWriteTimeUtc(final, File.GetLastWriteTimeUtc(source).AddHours(-3));
        }

        Profile profile = ProfileUnderTest(identity);

        // --- preview ---
        var simulated = await CreateDryRunEngine().SimulateAsync(profile, scopePath: null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        DryRunOperation previewOp = Assert.Single(report!.DestinationOperations);
        bool previewSaysUnchanged = previewOp.Kind == OperationKind.SkipUnchanged;

        // --- run ---
        JobCompletion completion = await CreateExecutor().ExecuteAsync(PlanFor(profile, source, final));
        bool runSaysUnchanged = completion is
        {
            Outcome: JobOutcome.Skipped,
            SkipReason: SkipReason.UnchangedAtAllTargets,
        };

        Assert.Equal(previewSaysUnchanged, runSaysUnchanged);

        // ...and both must match what the shared rule implies for this fixture.
        Assert.Equal(duplicate, runSaysUnchanged);

        // The destination content is the ground truth: unchanged means untouched, otherwise the source
        // bytes landed.
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(final));
    }

    private Profile ProfileUnderTest(LargeFileIdentity identity)
    {
        Profile profile = TestProfiles.Valid(sourcePath: _sourceDir, targetPath: _targetDir);
        return profile with
        {
            Policies = profile.Policies with
            {
                ConflictResolution = ConflictResolution.Overwrite,
                OnSuccess = OnSuccessAction.KeepSource,
                VerificationMethod = VerificationMethod.XxHash128,
                LargeFileIdentity = identity,
                LargeFileIdentityThresholdBytes = 0,   // every file takes the large-file path
            },
        };
    }

    private JobPlan PlanFor(Profile profile, string source, string final) =>
        JobFixtures.Execution(source, _sourceDir, [final], [_targetDir], new PolicySnapshot
        {
            Verification = profile.Policies.VerificationMethod,
            OverwriteHandling = profile.Policies.OverwriteHandling,
            ConflictResolution = profile.Policies.ConflictResolution,
            OnSuccess = profile.Policies.OnSuccess,
            ArchiveFolder = profile.Policies.ArchiveFolder,
            MetadataOnConflict = profile.Policies.MetadataOnConflict,
            LargeFileIdentity = profile.Policies.LargeFileIdentity,
            LargeFileIdentityThresholdBytes = profile.Policies.LargeFileIdentityThresholdBytes,
        }).Plan;

    private DryRunEngine CreateDryRunEngine()
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        FakeSettingsProvider settings = new(GlobalSettings.Default);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, settings);
        return new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new SourceScanner(TimeProvider.System, scheduler),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            settings,
            TimeProvider.System,
            new DestinationProjector(
                NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler));
    }

    private JobExecutor CreateExecutor()
    {
        FileHasher hasher = new(NullLogger<FileHasher>.Instance);
        PathLockRegistry locks = new();
        SourcePriorityRegistry priorities = new();
        SelfWriteSuppressionRegistry suppression = new(TimeProvider.System);
        TransientRetryPolicy retry = new(TimeProvider.System, NullLogger<TransientRetryPolicy>.Instance);
        AtomicPlacer placer = new(
            hasher, _journal, suppression, retry, new FakeMetadataPreserver(), priorities,
            TimeProvider.System, NullLogger<AtomicPlacer>.Instance);
        DispositionAuditLog audit = new(_paths, NullLogger<DispositionAuditLog>.Instance);

        return new JobExecutor(
            _journal,
            locks,
            new DiskPreflight(new FakeVolumeInfoProvider(), _config, NullLogger<DiskPreflight>.Instance),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            hasher,
            new ConflictResolver(locks, priorities, NullLogger<ConflictResolver>.Instance),
            placer,
            new RollbackExecutor(_journal, hasher, TimeProvider.System, NullLogger<RollbackExecutor>.Instance),
            new SourceDispositionService(
                new FakeTrashService(), audit, TimeProvider.System, NullLogger<SourceDispositionService>.Instance),
            new JobLogStore(_paths, TimeProvider.System, NullLogger<JobLogStore>.Instance),
            TimeProvider.System,
            NullLogger<JobExecutor>.Instance);
    }
}
