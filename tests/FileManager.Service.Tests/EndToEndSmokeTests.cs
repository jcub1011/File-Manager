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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private ServiceProvider? _provider;
    private IIpcServer? _server;
    private IJobOrchestrator? _orchestrator;
    private IDisposable? _eventBridge;
    private IDisposable? _profilesBridge;

    private string SourceDir => Path.Combine(_root, "source");
    private string TargetDir => Path.Combine(_root, "target");

    /// <summary>EngineHost is registered as a hosted service by AddEngine and takes this. Nothing here
    /// runs the host — this test drives the server and orchestrator directly — but the graph must still
    /// be satisfiable.</summary>
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", "fm-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);

        // Literally the graph Program.cs composes — EngineComposition.AddEngine — pointed at a temp
        // EnginePaths, rather than a hand-rebuilt copy of it. The copy this replaced had drifted three
        // handlers behind production (DryRunStreamHandler, RelocateProfilesHandler, ShutdownHandler),
        // so the "end to end" test exercised a dispatch table the product never shipped.
        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        foreach (string dir in new[] { paths.ProfilesDirectory, paths.LogsDirectory, paths.JobLogsDirectory, paths.JournalDirectory, paths.AuditDirectory, paths.StateDirectory, paths.WorkDirectory, paths.QuarantineDirectory })
            Directory.CreateDirectory(dir);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        EngineComposition.AddEngine(services, paths);
        // The only two deliberate substitutions, and the reason this cannot just resolve the production
        // graph as-is: the real implementations would recycle the developer's files and write to their
        // HKCU Run key. Registered AFTER AddEngine — MSDI is last-registration-wins.
        services.AddSingleton<ITrashService, NoopTrashService>();
        services.AddSingleton<IAutostartRegistrar, NoopAutostartRegistrar>();
        _provider = services.BuildServiceProvider();

        // No hand-written ProfilesDirectory pin here any more: SettingsService rebases both user-data
        // directories on the injected EnginePaths, so a temp root covers the profile store and the
        // dry-run scratch directory automatically. The old pin covered only profiles, and every future
        // harness had to remember to write it — forgetting it silently touched real user data.
        ISettingsProvider settings = _provider.GetRequiredService<ISettingsProvider>();
        Assert.StartsWith(paths.Root, settings.Current.ProfilesDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.Root, settings.Current.ScratchDirectory, StringComparison.OrdinalIgnoreCase);

        IProfileCatalog catalog = _provider.GetRequiredService<IProfileCatalog>();
        IEngineEventBus eventBus = _provider.GetRequiredService<IEngineEventBus>();
        _server = _provider.GetRequiredService<IIpcServer>();
        _orchestrator = _provider.GetRequiredService<IJobOrchestrator>();

        _eventBridge = eventBus.Subscribe(_server.Broadcast);
        // Same bridge EngineHost installs, so profiles-changed round-trips here too.
        _profilesBridge = catalog.Subscribe(() =>
            eventBus.Publish(new ProfilesChangedEvent { AtUtc = TimeProvider.System.GetUtcNow() }));
        Assert.True(_server.Start().IsSuccess);
        Assert.True(_orchestrator.Start().IsSuccess);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_orchestrator is not null)
            await _orchestrator.StopAsync();
        _profilesBridge?.Dispose();
        _eventBridge?.Dispose();
        if (_server is not null)
            await _server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        // Disposes every singleton the graph owns, including the journal and the scan scheduler.
        if (_provider is not null)
            await _provider.DisposeAsync();
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

            // streamed dry run — the route the UI actually uses (IpcGateway sends DryRunStreamRequest).
            // Its handler was absent from this test's hand-built dispatch table, so this path had never
            // been driven over a real pipe by the end-to-end suite at all.
            var streamed = await client.DryRunStreamAsync(new DryRunStreamRequest { ProfileId = profile.Id });
            Assert.True(streamed.TryGetValue(out DryRunReport? streamedReport));
            Assert.Equal(report.SourceFiles.Count, streamedReport!.SourceFiles.Count);
            string[] streamedDirs = DryRunDirectoryTable.Materialize(streamedReport.Directories);
            Assert.Contains(streamedReport.SourceOperations, o =>
                Path.Join(streamedDirs[o.DirIndex], o.FileName).EndsWith("fresh.txt")
                && o.Kind == OperationKind.Processed);

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
            var run = await client.RequestAsync<RunProfileResponse>(
                new RunProfileRequest { ProfileId = profile.Id, Path = sourceFile });
            Assert.True(run.TryGetValue(out RunProfileResponse? runResponse));
            Assert.Equal(1, runResponse!.QueuedCount);      // single file — the count is exact
            Assert.False(runResponse.Scanning);

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
            {
                Assert.Contains(events, e => e is JobStartedEvent);

                // Progress frames reach the wire for this job, ordered after its job-started. NOT
                // asserting a frame count or a specific phase: the publisher throttles, so a fast job
                // legitimately emits only one frame and pinning either would be a flaky test.
                int startedAt = events.FindIndex(e => e is JobStartedEvent);
                int progressAt = events.FindIndex(e => e is JobProgressEvent);
                Assert.True(progressAt > startedAt,
                    $"expected a job-progress after job-started; events=[{string.Join(", ", events.Select(e => e.GetType().Name))}]");
                JobProgressEvent progress = events.OfType<JobProgressEvent>().First();
                Assert.Equal(completedEvent.Job.JobId, progress.JobId);
                Assert.Equal(1, progress.TargetCount);
            }

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
    public async Task Run_profile_archiving_the_source_moves_the_original_over_the_real_pipe()
    {
        // The whole wired stack, end to end, for the disposition that MOVES the user's original file:
        // copy verified at the target, then the source relocated into the archive — not deleted, not
        // left behind, and reflected in the streamed completion event.
        string archiveDir = Path.Combine(_root, "archive");
        string sourceFile = Path.Combine(SourceDir, "invoice.pdf");
        File.WriteAllText(sourceFile, "the only copy");

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            Profile profile = NewProfile();
            profile = profile with
            {
                TargetLayout = TargetLayout.Flatten,
                Policies = profile.Policies with
                {
                    ConflictResolution = ConflictResolution.Overwrite,
                    OnSuccess = OnSuccessAction.MoveToArchive,
                    ArchiveFolder = archiveDir,
                },
            };
            var saved = await client!.RequestAsync<ValidationResponse>(
                new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = true });
            Assert.True(saved.TryGetValue(out ValidationResponse? validation));
            Assert.DoesNotContain(validation!.Issues, i => i.Severity == ValidationSeverity.Error);

            await using SubscriptionPump pump = await SubscriptionPump.StartAsync();

            var run = await client.RequestAsync<RunProfileResponse>(
                new RunProfileRequest { ProfileId = profile.Id, Path = sourceFile });
            Assert.True(run.IsSuccess);

            JobCompletedEvent completed = await pump.WaitForAsync<JobCompletedEvent>(TimeSpan.FromSeconds(15));
            Assert.Equal("Succeeded", completed.Job.Outcome);

            // The copy is at the target...
            string targetFile = Path.Combine(TargetDir, "invoice.pdf");
            Assert.True(await WaitForAsync(() => File.Exists(targetFile), TimeSpan.FromSeconds(10)),
                "the target copy was never placed");
            Assert.Equal("the only copy", File.ReadAllText(targetFile));

            // ...and the ORIGINAL is in the archive, not destroyed and not still in the source tree.
            string archived = Path.Combine(archiveDir, "invoice.pdf");
            Assert.True(await WaitForAsync(() => File.Exists(archived), TimeSpan.FromSeconds(10)),
                "the original was not archived");
            Assert.Equal("the only copy", File.ReadAllText(archived));
            Assert.False(File.Exists(sourceFile), "the original should have moved out of the source tree");
        }
    }

    [Fact]
    public async Task Run_profile_on_a_folder_streams_a_run_queued_event()
    {
        File.WriteAllText(Path.Combine(SourceDir, "one.txt"), "1");
        File.WriteAllText(Path.Combine(SourceDir, "two.txt"), "22");

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            Profile profile = NewProfile();
            await client!.RequestAsync<ValidationResponse>(
                new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = false });

            await using SubscriptionPump pump = await SubscriptionPump.StartAsync();

            // A folder run cannot report its count in the reply — enumeration is off the IPC thread.
            var run = await client.RequestAsync<RunProfileResponse>(
                new RunProfileRequest { ProfileId = profile.Id, Path = SourceDir });
            Assert.True(run.TryGetValue(out RunProfileResponse? runResponse));
            Assert.True(runResponse!.Scanning);
            Assert.Equal(0, runResponse.QueuedCount);

            RunQueuedEvent queued = await pump.WaitForAsync<RunQueuedEvent>(TimeSpan.FromSeconds(10));
            Assert.Equal(profile.Id, queued.ProfileId);
            Assert.Equal(2, queued.QueuedCount);
            Assert.Null(queued.Error);
        }
    }

    [Fact]
    public async Task Saving_a_profile_streams_a_profiles_changed_event()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            await using SubscriptionPump pump = await SubscriptionPump.StartAsync();

            await client!.RequestAsync<ValidationResponse>(
                new SaveProfileRequest { Profile = NewProfile(), AcknowledgeWarnings = false });

            await pump.WaitForAsync<ProfilesChangedEvent>(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A dedicated subscription connection plus the collected event stream — subscribe makes
    /// the connection one-way, so it can never be the one carrying requests.</summary>
    private sealed class SubscriptionPump : IAsyncDisposable
    {
        private readonly IpcClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<EngineEvent> _events = [];
        private readonly Task _pump;

        private SubscriptionPump(IpcClient client)
        {
            _client = client;
            _pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (EngineEvent evt in client.SubscribeAsync(_cts.Token))
                        lock (_events) _events.Add(evt);
                }
                catch (OperationCanceledException) { }
            });
        }

        public static async Task<SubscriptionPump> StartAsync()
        {
            var connected = await IpcClient.ConnectAsync();
            Assert.True(connected.TryGetValue(out IpcClient? client));
            SubscriptionPump pump = new(client!);
            await Task.Delay(300);   // let the subscription ack land before the caller acts
            return pump;
        }

        public async Task<T> WaitForAsync<T>(TimeSpan timeout) where T : EngineEvent
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_events)
                    if (_events.OfType<T>().FirstOrDefault() is { } found)
                        return found;
                await Task.Delay(25);
            }
            lock (_events)
                Assert.Fail($"no {typeof(T).Name} arrived; events=[{string.Join(", ", _events.Select(e => e.GetType().Name))}]");
            throw new InvalidOperationException("unreachable");
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _pump;
            await _client.DisposeAsync();
            _cts.Dispose();
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

            var run = await client.RequestAsync<RunProfileResponse>(new RunProfileRequest { ProfileId = profile.Id, Path = sourceFile });
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
