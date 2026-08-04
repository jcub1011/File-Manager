using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Audit;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Placement;
using FileManager.Core.Runs;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Scanning;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Stands up the <b>real</b> planning pipeline — real scan scheduler, real source scanner,
/// real filter compiler, real hasher, real conflict resolver, real destination projector, real
/// <see cref="ProfilePlanner"/> — over a real temp filesystem, and captures its output as a run
/// snapshot on disk. Only the platform boundary (volume info, settings) is faked.
/// <para>Integration by intent: the whole point of the snapshot is that a live run does exactly what
/// the preview described, so a harness that stubbed the planner would test nothing worth testing.</para></summary>
internal sealed class RunPlanHarness : IDisposable
{
    public RunPlanHarness(string label, int? maxScanThreads = 1)
    {
        Root = Path.Combine(Path.GetTempPath(), $"fm-{label}-" + Guid.NewGuid().ToString("N"));
        SourceDir = Path.Combine(Root, "source");
        TargetDir = Path.Combine(Root, "target");
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);
        Paths = new EnginePaths { Root = Path.Combine(Root, "engine") };
        Directory.CreateDirectory(Paths.RunsDirectory);

        ThreadBudget budget = maxScanThreads is int n ? ThreadBudget.Explicit(n) : ThreadBudget.Auto;
        GlobalSettings settings = new()
        {
            ScanThreading = new ScanThreadingSettings { MaxScanThreads = budget, PerDriveDefault = budget },
        };
        FakeSettingsProvider settingsProvider = new(settings);
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, settingsProvider);
        DestinationProjector projector = new(
            NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler);
        DryRunEngine engine = new(
            NullLogger<DryRunEngine>.Instance,
            new SourceScanner(TimeProvider.System, scheduler),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            settingsProvider,
            TimeProvider.System,
            projector);
        Planner = new ProfilePlanner(
            NullLogger<ProfilePlanner>.Instance, engine, projector, new FakeVolumeInfoProvider(), new EngineConfig());

        // The deletion half, over the REAL journal, the REAL audit log, and a real fake Recycle Bin
        // directory, so every assertion is about what is actually on disk afterwards.
        TrashBin = Path.Combine(Root, "recycle-bin");
        Trash = new FaultyTrashService(TrashBin);
        foreach (string dir in new[] { Paths.JournalDirectory, Paths.AuditDirectory, Paths.JobLogsDirectory })
            Directory.CreateDirectory(dir);
        _realJournal = new JobJournal(Paths, new EngineConfig(), NullLogger<JobJournal>.Instance);
        Journal = new FaultyJobJournal(_realJournal);
        Audit = new FaultyReconcileAuditLog(
            new MirrorDeletionAuditLog(Paths, NullLogger<MirrorDeletionAuditLog>.Instance));
        Locks = new PathLockRegistry();
        Suppression = new SelfWriteSuppressionRegistry(TimeProvider.System);
        Pause = new FakePauseState();
        JobLog = new JobLogStore(Paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);
        DeletionConfig = new EngineConfig();
        Pass = new MirrorDeletionPass(
            Journal, Locks, Suppression, Trash, Audit, Pause, JobLog, DeletionConfig,
            TimeProvider.System, NullLogger<MirrorDeletionPass>.Instance);
    }

    private readonly JobJournal _realJournal;

    public string Root { get; }
    public string SourceDir { get; }
    public string TargetDir { get; }
    public string TrashBin { get; }
    public EnginePaths Paths { get; }
    public ProfilePlanner Planner { get; }
    public FaultyTrashService Trash { get; }
    public FaultyJobJournal Journal { get; }
    public FaultyReconcileAuditLog Audit { get; }
    public PathLockRegistry Locks { get; }
    public SelfWriteSuppressionRegistry Suppression { get; }
    public FakePauseState Pause { get; }
    public JobLogStore JobLog { get; }
    public EngineConfig DeletionConfig { get; private set; }
    public MirrorDeletionPass Pass { get; private set; }

    /// <summary>Rebuilds the pass over a tweaked config, for the cap and ratio gates.</summary>
    public void UseConfig(EngineConfig config)
    {
        DeletionConfig = config;
        Pass = new MirrorDeletionPass(
            Journal, Locks, Suppression, Trash, Audit, Pause, JobLog, config,
            TimeProvider.System, NullLogger<MirrorDeletionPass>.Instance);
    }

    // ---- the run coordinator ---------------------------------------------------------------------

    private RunCoordinator? _coordinator;

    public RecordingEventBus Bus { get; } = new();

    public TriggerQueue Queue { get; private set; } = null!;

    /// <summary>A coordinator over the real planner, a real trigger queue, and the real deletion pass.
    /// Nothing consumes the queue — the tests drain it themselves and report settlement, which is what
    /// lets the barrier be driven deterministically instead of raced against a live orchestrator.</summary>
    public RunCoordinator Coordinator(EngineConfig? config = null)
    {
        if (_coordinator is not null)
            return _coordinator;
        Queue = new TriggerQueue(Pause, NullLogger<TriggerQueue>.Instance);
        EngineConfig effective = config ?? DeletionConfig;
        if (config is not null)
            UseConfig(config);
        _coordinator = new RunCoordinator(
            Planner, Queue, Pass, Bus, Paths, effective, TimeProvider.System,
            NullLogger<RunCoordinator>.Instance);
        return _coordinator;
    }

    /// <summary>Stands in for the orchestrator: waits for the run's payloads to be enqueued, then takes
    /// each and reports it settled with the given outcome.
    /// <para>Waiting matters — <c>Approve</c> returns immediately and the enqueue happens on the run's
    /// own background task, so draining "whatever is queued right now" reliably finds nothing and then
    /// starves the barrier. The target count comes from the run's own plan, which is exactly what the
    /// barrier is counting against.</para></summary>
    public async Task<List<Payload>> DrainAndSettleAsync(
        RunCoordinator coordinator,
        Guid runId,
        JobOutcome outcome = JobOutcome.Succeeded,
        Func<Payload, IReadOnlyList<string>>? finalPaths = null,
        int timeoutMs = 20_000)
    {
        int expected = coordinator.GetStatus(runId)?.PlannedCopies ?? 0;
        List<Payload> taken = [];
        for (int waited = 0; taken.Count < expected && waited < timeoutMs; waited += 20)
        {
            if (Queue.PendingCount == 0)
            {
                await Task.Delay(20);
                continue;
            }
            try
            {
                await foreach (Payload payload in Queue
                    .DequeueAsync(new CancellationTokenSource(200).Token).ConfigureAwait(false))
                {
                    taken.Add(payload);
                    if (payload.RunId is { } id)
                        coordinator.Settled(id, new JobCompletion(
                            JobId.New(), outcome, null, null, TimeSpan.Zero)
                        {
                            ResolvedFinalPaths = finalPaths?.Invoke(payload) ?? [],
                        });
                    break;   // one per iteration keeps the loop's accounting obvious
                }
            }
            catch (OperationCanceledException)
            {
                // The short-lived token expired waiting on an empty queue; the outer loop re-checks.
            }
        }
        return taken;
    }

    /// <summary>Polls until a run reaches a phase, or fails the test.</summary>
    public static async Task<RunStatus> WaitForPhaseAsync(
        RunCoordinator coordinator, Guid runId, RunPhase phase, int timeoutMs = 15_000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            RunStatus? status = coordinator.GetStatus(runId);
            if (status is not null && status.Phase == phase)
                return status;
            await Task.Delay(25);
        }
        RunStatus? last = coordinator.GetStatus(runId);
        Assert.Fail($"run {runId} never reached {phase}; last was {last?.Phase.ToString() ?? "unknown"}"
            + (last?.PlanError is { } e ? $" (plan error: {e})" : ""));
        return null!;
    }

    /// <summary>Everything currently in the fake Recycle Bin. A test asserting a deletion happened
    /// checks BOTH that the destination is gone and that the file turned up here — recycled, not
    /// destroyed.</summary>
    public IReadOnlyList<string> Bin() =>
        Directory.Exists(TrashBin) ? Directory.GetFiles(TrashBin) : [];

    public IReadOnlyList<JournalRecord> JournalRecords()
    {
        Journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        return records ?? [];
    }

    public IReadOnlyList<MirrorDeletionAuditRecord> AuditRows()
    {
        Audit.ReadRecent(1000).TryGetValue(out IReadOnlyList<MirrorDeletionAuditRecord>? rows);
        return rows ?? [];
    }

    /// <summary>Builds a deletion request from a planned snapshot, with every gate satisfied by default
    /// so a test overrides exactly the one it is exercising.</summary>
    public MirrorDeletionRequest Request(
        string runDirectory,
        IReadOnlyList<RunDeleteItem>? orphans = null,
        string? scopePath = null,
        bool planTruncated = false,
        bool enumerationIncomplete = false,
        int copyJobsFailed = 0,
        IReadOnlySet<string>? pathsWritten = null,
        IReadOnlyDictionary<string, int>? sweptByRoot = null,
        Profile? profile = null)
    {
        RunSnapshotHeader header = Header(runDirectory);
        IReadOnlyList<RunDeleteItem> items = orphans ?? Deletes(runDirectory);
        return new MirrorDeletionRequest
        {
            PassId = JobId.New(),
            RunId = header.RunId,
            Profile = profile ?? header.Profile,
            Orphans = items,
            ScopePath = scopePath ?? header.ScopePath,
            PlanTruncated = planTruncated || header.Truncated,
            EnumerationIncomplete = enumerationIncomplete,
            CopyJobsFailed = copyJobsFailed,
            PathsWrittenByThisRun = pathsWritten ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            // Default: a swept count that keeps the ratio guard quiet, so only the test that targets it
            // has to think about it.
            SweptFilesByTargetRoot = sweptByRoot ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [TargetDir] = Math.Max(1, items.Count * 10),
            },
        };
    }

    /// <summary>A Mirror profile over this harness's source/target pair. Mirror forces the destination
    /// sweep on, which is the only thing that produces orphan deletions.</summary>
    public Profile MirrorProfile(
        MirrorDeletion timing = MirrorDeletion.AfterCopy,
        ConflictResolution conflict = ConflictResolution.Overwrite,
        FilterSet? filters = null) =>
        TestProfiles.Valid(SourceDir, TargetDir) with
        {
            SyncMode = SyncMode.Mirror,
            ScanDestination = true,
            Filters = filters,
            Policies = TestProfiles.DefaultPolicies() with
            {
                ConflictResolution = conflict,
                MirrorDeletion = timing,
            },
        };

    public Profile AdditiveProfile(bool scanDestination = false) =>
        TestProfiles.Valid(SourceDir, TargetDir) with { ScanDestination = scanDestination };

    public string WriteSource(string relativePath, string content) =>
        Write(Path.Combine(SourceDir, relativePath), content);

    public string WriteTarget(string relativePath, string content) =>
        Write(Path.Combine(TargetDir, relativePath), content);

    public static string Write(string absolutePath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);
        return absolutePath;
    }

    /// <summary>Plans the profile and writes the snapshot, returning the run directory. Mirrors what
    /// the run coordinator's planning phase does, so the tests exercise that sequence rather than a
    /// convenience shortcut.</summary>
    public async Task<(string RunDirectory, PlanState State, Result Completion)> PlanAsync(
        Profile profile, string? scopePath = null, Guid? runId = null, int? maxFiles = null)
    {
        Guid id = runId ?? Guid.NewGuid();
        string directory = RunSnapshotPaths.DirectoryFor(Paths, id);
        PlanState state = maxFiles is int cap ? new PlanState { MaxFiles = cap } : new PlanState();
        using RunSnapshotWriter writer = new(directory, NullLogger.Instance);

        await foreach (Result<PlanChunk, string> chunk in Planner.PlanAsync(profile, scopePath, state))
        {
            Assert.False(chunk.TryGetError(out string? error), error);
            chunk.TryGetValue(out PlanChunk planned);
            writer.Consume(planned, profile);
        }

        Result completion = writer.Complete(new RunSnapshotHeader
        {
            RunId = id,
            Profile = profile,
            ScopePath = scopePath,
            PlannedAtUtc = DateTimeOffset.UnixEpoch,
            CopyItemCount = writer.CopyCount,
            DeleteItemCount = writer.DeleteCount,
            CopyBytes = writer.CopyBytes,
            DeleteBytes = writer.DeleteBytes,
            SourceItemCount = writer.SourceCount,
            DestinationItemCount = writer.DestinationCount,
            Truncated = state.Truncated,
            SweepFaultDetail = state.SweepFaultDetail,
            Space = state.Space,
        });
        return (directory, state, completion);
    }

    public static RunSnapshotHeader Header(string runDirectory)
    {
        Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(runDirectory);
        Assert.False(read.TryGetError(out string? error), error);
        read.TryGetValue(out RunSnapshotHeader? header);
        return header!;
    }

    public static List<RunCopyItem> Copies(string runDirectory) =>
        [.. RunSnapshotReader.ReadCopies(runDirectory, NullLogger.Instance)];

    public static List<RunDeleteItem> Deletes(string runDirectory) =>
        [.. RunSnapshotReader.ReadDeletes(runDirectory, NullLogger.Instance)];

    public static List<RunSourceItem> Sources(string runDirectory) =>
        [.. RunSnapshotReader.ReadSources(runDirectory, NullLogger.Instance)];

    public static List<RunDestinationItem> Destinations(string runDirectory) =>
        [.. RunSnapshotReader.ReadDestinations(runDirectory, NullLogger.Instance)];

    public void Dispose()
    {
        Suppression.Dispose();
        _realJournal.Dispose();
        if (!Directory.Exists(Root))
            return;
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a lingering handle must not fail the test it just verified */ }
    }
}
