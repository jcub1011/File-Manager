using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Contracts.Primitives;
using FileManager.Core.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Placement;
using FileManager.Core.Platform;
using FileManager.Core.Preflight;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Scanning;
using FileManager.Core.Watching;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace FileManager.Service.Tests;

/// <summary>The whole slice over a real named pipe: the real Core graph (now including the live
/// single-job vertical — trigger queue, orchestrator, executor, event bus) behind IpcServer +
/// WindowsIpcEndpointProvider, driven by the real Contracts IpcClient. Covers the profile lifecycle
/// + dry-run, a manual run that actually moves a file with a streamed completion event, and
/// pause-then-resume.</summary>
[SupportedOSPlatform("windows")]
[Collection(PipeCollection.Name)]
public sealed class EndToEndSmokeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-e2e-" + Guid.NewGuid().ToString("N"));
    private IpcServer? _server;
    private JobOrchestrator? _orchestrator;
    private JobJournal? _journal;
    private IDisposable? _eventBridge;

    private string SourceDir => Path.Combine(_root, "source");
    private string TargetDir => Path.Combine(_root, "target");

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", "fm-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);

        // The same graph Program.cs composes, pointed at a temp EnginePaths.
        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        foreach (string dir in new[] { paths.ProfilesDirectory, paths.LogsDirectory, paths.JobLogsDirectory, paths.JournalDirectory, paths.AuditDirectory, paths.StateDirectory, paths.WorkDirectory, paths.QuarantineDirectory })
            Directory.CreateDirectory(dir);

        EngineConfig config = new();
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        FilterCompiler filterCompiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
        ProfileValidator validator = new(NullLogger<ProfileValidator>.Instance, filterCompiler);
        SettingsService settings = new(NullLogger<SettingsService>.Instance, paths);
        // The store resolves its directory from settings; pin it under the temp engine root so the
        // smoke test never touches the real %LOCALAPPDATA% profiles directory.
        settings.Update(settings.Current with { ProfilesDirectory = Path.Combine(paths.Root, "profiles") });
        ProfileStore store = new(NullLogger<ProfileStore>.Instance, settings, validator);
        ProfileCatalog catalog = new(NullLogger<ProfileCatalog>.Instance, store);
        FileHasher hasher = new(NullLogger<FileHasher>.Instance);

        // The lock/priority registries are SHARED by the conflict resolver, the placer, and the
        // executor — a job's held lock set and the resolver's suffix-probe must hit one registry.
        PathLockRegistry lockRegistry = new();
        SourcePriorityRegistry priorities = new();
        ConflictResolver resolver = new(lockRegistry, priorities, NullLogger<ConflictResolver>.Instance);
        WindowsVolumeInfoProvider volumes = new(NullLogger<WindowsVolumeInfoProvider>.Instance);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, settings);
        SourceScanner scanner = new(TimeProvider.System, scheduler, volumes);
        DestinationProjector destinationProjector = new(NullLogger<DestinationProjector>.Instance, volumes, scheduler);
        DryRunEngine dryRun = new(NullLogger<DryRunEngine>.Instance, scanner, filterCompiler, hasher, resolver, settings, TimeProvider.System, destinationProjector);

        // Live single-job vertical.
        _journal = new JobJournal(paths, config, NullLogger<JobJournal>.Instance);
        SelfWriteSuppressionRegistry suppression = new(TimeProvider.System);
        TransientRetryPolicy retry = new(TimeProvider.System, NullLogger<TransientRetryPolicy>.Instance);
        WindowsMetadataPreserver metadata = new(NullLogger<WindowsMetadataPreserver>.Instance);
        AtomicPlacer placer = new(hasher, _journal, suppression, retry, metadata, priorities, TimeProvider.System, NullLogger<AtomicPlacer>.Instance);
        DiskPreflight preflight = new(volumes, config, NullLogger<DiskPreflight>.Instance);
        RollbackExecutor rollback = new(_journal, hasher, TimeProvider.System, NullLogger<RollbackExecutor>.Instance);
        DispositionAuditLog audit = new(paths, NullLogger<DispositionAuditLog>.Instance);
        SourceDispositionService disposition = new(new NoopTrashService(), audit, TimeProvider.System, NullLogger<SourceDispositionService>.Instance);

        PauseStateService pauseState = new(paths, NullLogger<PauseStateService>.Instance);
        EngineEventBus eventBus = new(NullLogger<EngineEventBus>.Instance);
        JobLogStore jobLog = new(paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);
        TriggerQueue triggerQueue = new(pauseState, NullLogger<TriggerQueue>.Instance);
        ProfileMatcher matcher = new(catalog, filterCompiler, NullLogger<ProfileMatcher>.Instance);
        JobPlanFactory planFactory = new(paths, config);
        JobExecutor executor = new(_journal, lockRegistry, preflight, filterCompiler, hasher, resolver, placer, rollback, disposition, jobLog, TimeProvider.System, NullLogger<JobExecutor>.Instance);
        _orchestrator = new JobOrchestrator(triggerQueue, catalog, executor, planFactory, eventBus, jobLog, pauseState, config, TimeProvider.System, NullLogger<JobOrchestrator>.Instance);

        IIpcRequestHandler[] handlers =
        [
            new GetStatusHandler(_orchestrator),
            new GetMatchingProfilesHandler(matcher),
            new RunProfileHandler(catalog, triggerQueue, scanner, TimeProvider.System, NullLogger<RunProfileHandler>.Instance),
            new SetPausedHandler(pauseState, NullLogger<SetPausedHandler>.Instance),
            new GetRecentJobsHandler(jobLog),
            new GetJobLogHandler(jobLog),
            new ListProfilesHandler(catalog),
            new GetProfileHandler(NullLogger<GetProfileHandler>.Instance, catalog),
            new SaveProfileHandler(NullLogger<SaveProfileHandler>.Instance, store, catalog),
            new DeleteProfileHandler(NullLogger<DeleteProfileHandler>.Instance, store, catalog),
            new ValidateProfileHandler(validator, catalog),
            new DryRunHandler(NullLogger<DryRunHandler>.Instance, dryRun, catalog),
            new GetSettingsHandler(settings),
            new UpdateSettingsHandler(NullLogger<UpdateSettingsHandler>.Instance, settings, new NoopAutostartRegistrar()),
        ];
        _server = new IpcServer(
            NullLogger<IpcServer>.Instance,
            new WindowsIpcEndpointProvider(NullLogger<WindowsIpcEndpointProvider>.Instance),
            handlers.ToDictionary(h => h.RequestType));

        _eventBridge = eventBus.Subscribe(_server.Broadcast);
        Assert.True(_server.Start().IsSuccess);
        Assert.True(_orchestrator.Start().IsSuccess);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_orchestrator is not null)
            await _orchestrator.StopAsync();
        _eventBridge?.Dispose();
        if (_server is not null)
            await _server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        _journal?.Dispose();
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Full_profile_lifecycle_and_dry_run_over_the_pipe()
    {
        File.WriteAllText(Path.Combine(SourceDir, "fresh.txt"), "new content");
        File.WriteAllText(Path.Combine(SourceDir, "same.txt"), "identical");
        File.WriteAllText(Path.Combine(TargetDir, "same.txt"), "identical");

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            // status
            var status = await client!.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(status.TryGetValue(out StatusResponse? statusResponse));
            Assert.Equal(0, statusResponse!.Status.ActiveProfiles);
            Assert.False(statusResponse.Status.Paused);

            // save
            Profile profile = NewProfile();
            var saved = await client.RequestAsync<ValidationResponse>(
                new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = false });
            Assert.True(saved.TryGetValue(out ValidationResponse? validation));
            Assert.Empty(validation!.Issues);

            // list
            var listed = await client.RequestAsync<ProfileListResponse>(new ListProfilesRequest());
            Assert.True(listed.TryGetValue(out ProfileListResponse? list));
            Assert.Single(list!.Profiles);
            Assert.Equal(profile.Id, list.Profiles[0].ProfileId);

            // get
            var got = await client.RequestAsync<ProfileResponse>(new GetProfileRequest { ProfileId = profile.Id });
            Assert.True(got.TryGetValue(out ProfileResponse? getResponse));
            Assert.Equal(profile.Name, getResponse!.Profile.Name);

            // dry run
            var dryRun = await client.RequestAsync<DryRunResponse>(new DryRunRequest { ProfileId = profile.Id });
            Assert.True(dryRun.TryGetValue(out DryRunResponse? dryRunResponse));
            DryRunReport report = dryRunResponse!.Report;
            Assert.Equal(2, report.SourceFiles.Count);
            string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
            string PathOf(DryRunOperation o) => Path.Join(dirPaths[o.DirIndex], o.FileName);
            Assert.Contains(report.SourceOperations, o =>
                PathOf(o).EndsWith("fresh.txt") && o.Kind == OperationKind.Processed);
            Assert.Contains(report.SourceOperations, o =>
                PathOf(o).EndsWith("same.txt") && o.Kind == OperationKind.SkippedUnchanged);

            // status now reflects the active profile
            var status2 = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(status2.TryGetValue(out StatusResponse? status2Response));
            Assert.Equal(1, status2Response!.Status.ActiveProfiles);

            // protocol version mismatch
            var mismatched = await client.RequestAsync<StatusResponse>(
                new GetStatusRequest { ProtocolVersion = 99 });
            Assert.True(mismatched.TryGetError(out IpcError? versionError));
            Assert.Equal("IPC_VERSION_MISMATCH", versionError!.Code);

            // delete
            var deleted = await client.RequestAsync<OkResponse>(new DeleteProfileRequest { ProfileId = profile.Id });
            Assert.True(deleted.IsSuccess);
            var gone = await client.RequestAsync<ProfileResponse>(new GetProfileRequest { ProfileId = profile.Id });
            Assert.True(gone.TryGetError(out IpcError? notFound));
            Assert.Equal("PROFILE_NOT_FOUND", notFound!.Code);
        }
    }

    [Fact]
    public async Task Run_profile_moves_a_file_and_streams_a_completed_event()
    {
        string sourceFile = Path.Combine(SourceDir, "report.txt");
        File.WriteAllText(sourceFile, "the payload");

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            Profile profile = NewProfile();
            var saved = await client!.RequestAsync<ValidationResponse>(
                new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = false });
            Assert.True(saved.TryGetValue(out ValidationResponse? validation) && validation!.Issues.Count == 0);

            // Subscribe on a second connection and collect events until a JobCompleted arrives.
            var subConnected = await IpcClient.ConnectAsync();
            Assert.True(subConnected.TryGetValue(out IpcClient? subClient));
            using var subCts = new CancellationTokenSource();
            TaskCompletionSource<JobCompletedEvent> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<EngineEvent> events = [];
            Task pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (EngineEvent evt in subClient!.SubscribeAsync(subCts.Token))
                    {
                        lock (events) events.Add(evt);
                        if (evt is JobCompletedEvent jce) completed.TrySetResult(jce);
                    }
                }
                catch (OperationCanceledException) { }
            });
            await Task.Delay(300);   // let the subscription's ack land before the run

            // Run.
            var run = await client.RequestAsync<OkResponse>(
                new RunProfileRequest { ProfileId = profile.Id, Path = sourceFile });
            Assert.True(run.IsSuccess);

            // The file appears at the target with identical content.
            string targetFile = Path.Combine(TargetDir, "report.txt");
            bool placed = await WaitForAsync(() => File.Exists(targetFile), TimeSpan.FromSeconds(10));
            if (!placed)
            {
                var diag = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
                diag.TryGetValue(out StatusResponse? diagStatus);
                lock (events)
                    Assert.Fail($"target file never placed. lastError={diagStatus?.Status.LastError}; events=[{string.Join(", ", events.Select(e => e.GetType().Name))}]");
            }
            Assert.Equal("the payload", File.ReadAllText(targetFile));
            Assert.True(File.Exists(sourceFile), "OnSuccess=KeepSource must leave the source in place");

            // A JobCompleted event streamed to the subscriber.
            JobCompletedEvent completedEvent = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Succeeded", completedEvent.Job.Outcome);
            Assert.EndsWith("report.txt", completedEvent.Job.SourcePath);
            lock (events)
                Assert.Contains(events, e => e is JobStartedEvent);

            // Recent jobs + the per-job log are queryable.
            var recent = await client.RequestAsync<RecentJobsResponse>(new GetRecentJobsRequest { Count = 10 });
            Assert.True(recent.TryGetValue(out RecentJobsResponse? recentResponse));
            Assert.Contains(recentResponse!.Jobs, j => j.Outcome == "Succeeded" && j.SourcePath.EndsWith("report.txt"));

            var log = await client.RequestAsync<JobLogResponse>(new GetJobLogRequest { JobId = completedEvent.Job.JobId });
            Assert.True(log.TryGetValue(out JobLogResponse? logResponse));
            Assert.NotEmpty(logResponse!.Lines);

            subCts.Cancel();
            await pump;
            await subClient!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Paused_run_queues_until_resumed()
    {
        string sourceFile = Path.Combine(SourceDir, "held.txt");
        File.WriteAllText(sourceFile, "queued content");

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            Profile profile = NewProfile();
            await client!.RequestAsync<ValidationResponse>(new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = false });

            // Pause, then run: the payload queues and nothing moves.
            var paused = await client.RequestAsync<OkResponse>(new SetPausedRequest { Paused = true });
            Assert.True(paused.IsSuccess);

            var run = await client.RequestAsync<OkResponse>(new RunProfileRequest { ProfileId = profile.Id, Path = sourceFile });
            Assert.True(run.IsSuccess);

            string targetFile = Path.Combine(TargetDir, "held.txt");
            Assert.True(await WaitForAsync(() => QueuedCount(client) >= 1, TimeSpan.FromSeconds(5)), "the payload never queued");
            await Task.Delay(300);
            Assert.False(File.Exists(targetFile), "a paused engine must not place the file");

            // Resume: the queued payload drains and the file lands.
            var resumed = await client.RequestAsync<OkResponse>(new SetPausedRequest { Paused = false });
            Assert.True(resumed.IsSuccess);
            Assert.True(await WaitForAsync(() => File.Exists(targetFile), TimeSpan.FromSeconds(10)), "resume did not drain the queued payload");
            Assert.Equal("queued content", File.ReadAllText(targetFile));
        }
    }

    private static int QueuedCount(IpcClient client)
    {
        var status = client.RequestAsync<StatusResponse>(new GetStatusRequest()).GetAwaiter().GetResult();
        return status.TryGetValue(out StatusResponse? response) ? response!.Status.QueuedPayloads : 0;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task Global_settings_round_trip_and_persist_over_the_pipe()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            // default before any save
            var initial = await client!.RequestAsync<SettingsResponse>(new GetSettingsRequest());
            Assert.True(initial.TryGetValue(out SettingsResponse? initialResponse));
            Assert.True(initialResponse!.Settings.ScanThreading.MaxScanThreads.IsAuto);

            // update → echoed back and written to disk
            var updated = await client.RequestAsync<SettingsResponse>(new UpdateSettingsRequest
            {
                Settings = new GlobalSettings
                {
                    ScanThreading = new ScanThreadingSettings { MaxScanThreads = ThreadBudget.Explicit(3) },
                },
            });
            Assert.True(updated.TryGetValue(out SettingsResponse? updatedResponse));
            Assert.Equal(3, updatedResponse!.Settings.ScanThreading.MaxScanThreads.Value);
            Assert.True(File.Exists(Path.Combine(_root, "engine", "settings.json")));

            // a subsequent read reflects the persisted value
            var reread = await client.RequestAsync<SettingsResponse>(new GetSettingsRequest());
            Assert.True(reread.TryGetValue(out SettingsResponse? rereadResponse));
            Assert.Equal(3, rereadResponse!.Settings.ScanThreading.MaxScanThreads.Value);
        }
    }

    private Profile NewProfile() => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "e2e",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = SourceDir }],
        Targets = [new TargetConfig { Path = TargetDir }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.Skip,
            OverwriteHandling = OverwriteHandling.StageOverwrites,
            VerificationMethod = VerificationMethod.Sha256,
            OnSuccess = OnSuccessAction.KeepSource,
            ArchiveFolder = null,
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        Filters = null,
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresAndSkips, NotifyOnFailure = true },
    };
}

/// <summary>No-op autostart so the e2e test never mutates the real HKCU Run key.</summary>
internal sealed class NoopAutostartRegistrar : IAutostartRegistrar
{
    public Result RegisterAutostart() => Result.Success();
    public Result UnregisterAutostart() => Result.Success();
}

/// <summary>No-op trash so the e2e test never touches the real Recycle Bin (all profiles use
/// OnSuccess=KeepSource anyway).</summary>
internal sealed class NoopTrashService : ITrashService
{
    public Result MoveToTrash(string absolutePath) => Result.Success();
}
