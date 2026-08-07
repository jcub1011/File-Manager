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
using FileManager.Core.Profiles;
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
    /// <param name="maxScanDepth">Lowers the scan's depth ceiling so a test can reach a genuine
    /// <c>SweepFaulted</c> plan with a handful of nested directories (see <see cref="WriteDeepTarget"/>).
    /// This is the ONLY way a plan is truncated now that the file-count cap is gone, so the truncation
    /// tests have to produce a real unwalkable tree rather than dial a number down.
    /// <c>ScanThreadResolver.ResolveMaxScanDepth</c> clamps at 8, so 8 is the lowest useful value.</param>
    public RunPlanHarness(string label, int? maxScanThreads = 1, int? maxScanDepth = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"fm-{label}-" + Guid.NewGuid().ToString("N"));
        SourceDir = Path.Combine(Root, "source");
        TargetDir = Path.Combine(Root, "target");
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);
        Paths = new EnginePaths { Root = Path.Combine(Root, "engine") };
        Directory.CreateDirectory(Paths.RunsDirectory);

        ThreadBudget budget = maxScanThreads is int n ? ThreadBudget.Explicit(n) : ThreadBudget.Auto;
        GlobalSettings defaults = GlobalSettings.Default;
        GlobalSettings settings = new()
        {
            ScanThreading = new ScanThreadingSettings { MaxScanThreads = budget, PerDriveDefault = budget },
            MaxScanDepth = maxScanDepth ?? defaults.MaxScanDepth,
        };
        FakeSettingsProvider settingsProvider = new(settings);
        // Kept so a retention test can rewrite the auto-delete settings the coordinator reads on every
        // sweep, which is how it picks up a change without a restart.
        SettingsProvider = settingsProvider;
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
            TimeProvider.System, NullLogger<MirrorDeletionPass>.Instance, RunPause);
    }

    private readonly JobJournal _realJournal;

    public string Root { get; }
    public string SourceDir { get; }
    public string TargetDir { get; }
    public string TrashBin { get; }
    public EnginePaths Paths { get; }
    /// <summary>The planning pipeline the coordinator drives. Real by default — planning over a real temp
    /// filesystem is the point of this harness — but settable, so a test about the coordinator's OWN loop
    /// (its progress sampling, its cancellation) can substitute a planner whose timing it controls. Set it
    /// before <see cref="Coordinator"/>, which caches. Note that <see cref="PlanAsync"/> uses this too.</summary>
    public IProfilePlanner Planner { get; set; }
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
            TimeProvider.System, NullLogger<MirrorDeletionPass>.Instance, RunPause);
    }

    // ---- the run coordinator ---------------------------------------------------------------------

    private RunCoordinator? _coordinator;

    public RecordingEventBus Bus { get; } = new();

    public TriggerQueue Queue { get; private set; } = null!;

    /// <summary>A coordinator over the real planner, a real trigger queue, and the real deletion pass.
    /// Nothing consumes the queue — the tests drain it themselves and report settlement, which is what
    /// lets the barrier be driven deterministically instead of raced against a live orchestrator.</summary>
    /// <summary>What the coordinator's approval gate sees. Empty by default so a test that is not about
    /// validation approves as it always did; set it before <see cref="Coordinator"/> to drive the gate.</summary>
    public StubProfileValidator Validator { get; set; } = new();

    /// <summary>The catalog the validator's "other active profiles" argument comes from. Empty is the
    /// honest default for a run planned from a draft, which is in no catalog.</summary>
    public IProfileCatalog Catalog { get; set; } = new FakeProfileCatalog();

    /// <summary>The per-run pause flags, shared by the coordinator (which writes), the trigger queue and
    /// the deletion pass (which read) — exactly as the real composition wires them. One instance, or a
    /// pause set through the coordinator would be invisible to the queue that has to honour it.</summary>
    public RunPauseRegistry RunPause { get; } = new(NullLogger<RunPauseRegistry>.Instance);

    /// <summary>The settings the coordinator reads for its retention rules. Mutable so a test can turn
    /// auto-delete off, or shorten the interval, mid-run.</summary>
    public FakeSettingsProvider SettingsProvider { get; }

    /// <summary>The coordinator's clock. Assign a <c>FakeTimeProvider</c> before <see cref="Coordinator"/>
    /// to drive the drain barrier's deadline deterministically — the only way to assert that a long pause
    /// does not time it out without waiting out a real one.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    public RunCoordinator Coordinator(EngineConfig? config = null)
    {
        if (_coordinator is not null)
            return _coordinator;
        Queue = new TriggerQueue(Pause, NullLogger<TriggerQueue>.Instance, RunPause);
        EngineConfig effective = config ?? DeletionConfig;
        if (config is not null)
            UseConfig(config);
        _coordinator = new RunCoordinator(
            Planner, Queue, Pass, Bus, Validator, Catalog, Paths, effective, Time,
            RunPause, SettingsProvider, NullLogger<RunCoordinator>.Instance);
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
                            // The real executor reports the source file's size on every completion, and the
                            // run's byte progress is summed from it. Reported here too, from the file on
                            // disk, so the coordinator's byte accounting is exercised rather than fed zeros.
                            SourceBytes = File.Exists(payload.SourcePath)
                                ? new FileInfo(payload.SourcePath).Length
                                : 0,
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

    /// <summary>Polls until a condition holds, or fails the test naming what it was waiting for.
    ///
    /// <para>For anything a run does on its DETACHED background task — the enqueue above all, which
    /// <c>Approve</c> does not wait for. A fixed <c>Task.Delay</c> before asserting that work HAS happened
    /// is a race the machine wins under load, and it fails as a puzzling value mismatch rather than as a
    /// timeout. (Sleeping to assert something has NOT happened is fine and stays: load only makes that
    /// direction safer.)</para></summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"timed out after {timeoutMs} ms waiting for {what}");
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
        Profile? profile = null,
        Guid? runId = null)
    {
        RunSnapshotHeader header = Header(runDirectory);
        IReadOnlyList<RunDeleteItem> items = orphans ?? Deletes(runDirectory);
        // Derived from the items rather than taken from the header, because a test that supplies its own
        // orphan list expects the counts to describe THAT list. Production reads all three straight off
        // the header, which is what lets the pass stream the orphans instead of materializing them.
        Dictionary<string, int> orphansByRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (RunDeleteItem item in items)
            orphansByRoot[item.TargetRoot] = orphansByRoot.GetValueOrDefault(item.TargetRoot) + 1;
        return new MirrorDeletionRequest
        {
            PassId = JobId.New(),
            // Overridable so a per-run-pause test can hold a pass by an id it controls, rather than having
            // to reach back into the snapshot header for one.
            RunId = runId ?? header.RunId,
            Profile = profile ?? header.Profile,
            Orphans = items,
            OrphanCount = items.Count,
            OrphanBytes = items.Sum(i => i.SizeBytes),
            OrphansByTargetRoot = orphansByRoot,
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

    /// <summary>Writes one file <paramref name="depth"/> directory levels below the target root, so a
    /// harness built with a lowered <c>maxScanDepth</c> produces a sweep that genuinely cannot finish
    /// walking the tree — <c>ScanScheduler.ReportDepthCeiling</c> emits a Warning fault, the projector
    /// folds it into <c>SweepFaulted</c>, and the planner marks the plan truncated.
    ///
    /// <para>The ceiling prunes a child at <c>depth &gt; MaxScanDepth</c> counting the root as 0, so
    /// <paramref name="depth"/> must exceed the harness's ceiling for this to fault at all.</para></summary>
    public string WriteDeepTarget(int depth, string fileName = "deep.txt", string content = "deep")
    {
        string path = TargetDir;
        for (int i = 1; i <= depth; i++)
            path = Path.Combine(path, $"l{i}");
        return Write(Path.Combine(path, fileName), content);
    }

    /// <summary>Plans the profile and writes the snapshot, returning the run directory. Mirrors what
    /// the run coordinator's planning phase does, so the tests exercise that sequence rather than a
    /// convenience shortcut.</summary>
    /// <param name="diskReserveBytes">The snapshot volume's free-space floor. Zero (the default) means
    /// the guard can never fire, which is what every test that is not ABOUT the guard wants — a temp
    /// volume's free space is not something a test may assume anything about.</param>
    public async Task<(string RunDirectory, PlanState State, Result Completion)> PlanAsync(
        Profile profile, string? scopePath = null, Guid? runId = null, long diskReserveBytes = 0)
    {
        Guid id = runId ?? Guid.NewGuid();
        string directory = RunSnapshotPaths.DirectoryFor(Paths, id);
        PlanState state = new();
        using RunSnapshotWriter writer = new(directory, NullLogger.Instance, diskReserveBytes);

        await foreach (Result<PlanChunk, string> chunk in Planner.PlanAsync(profile, scopePath, state))
        {
            Assert.False(chunk.TryGetError(out string? error), error);
            chunk.TryGetValue(out PlanChunk planned);
            writer.Consume(planned, profile);
        }

        // Through the writer's own factory, NOT a header built field by field here. This used to be a
        // second construction site, and it silently fell behind every count the writer gained — so the
        // tests were asserting against a header the production path would have filled in differently.
        Result completion = writer.Complete(
            writer.Header(id, profile, scopePath, DateTimeOffset.UnixEpoch, state));
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
