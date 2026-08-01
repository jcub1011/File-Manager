using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core;
using FileManager.Core.IPC;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Observability;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Service.Tests;

/// <summary>The §2.4 startup sequence. Its ordering is load-bearing and was previously pinned by
/// nothing: I-RECOVER-FIRST (crash recovery must complete BEFORE the IPC server accepts connections or
/// the orchestrator can take a path lock, or a new job can grab files recovery was about to restore),
/// and the bridges-after-Reload rule (a catalog bridge created earlier would publish a spurious
/// profiles-changed at startup). CrashRecovery itself is heavily tested; that it is called first was
/// not. Nor were the three early exits — none had ever been executed by a test.</summary>
public sealed class EngineHostStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _order = [];
    private readonly string _mutexName = @"Local\fm-test-" + Guid.NewGuid().ToString("N");

    public EngineHostStartupTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>BackgroundService.StartAsync schedules ExecuteAsync rather than entering it inline, so
    /// the sequence runs on another thread and every assertion below waits for it. Snapshot under the
    /// lock the doubles write under — this is cross-thread, not just concurrent.</summary>
    private List<string> Order()
    {
        lock (_order) return [.. _order];
    }

    private async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>Waits for the startup sequence to settle, then asserts nothing more arrives — so a step
    /// running LATE (after the one that must follow it) still fails.</summary>
    private async Task<List<string>> SettledOrderAsync(int expectedCount)
    {
        await WaitForAsync(() => Order().Count >= expectedCount);
        await Task.Delay(50);
        return Order();
    }

    // ---- recording doubles: each appends its own name to one shared ordering log ------------------

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private int _stopCalls;
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref _stopCalls);
    }

    private sealed class RecordingCatalog(List<string> order, Result? reloadResult = null) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = [];
        public IReadOnlyList<Profile> Active { get; } = [];

        public IDisposable Subscribe(Action changeHandler)
        {
            lock (order) order.Add("catalog.Subscribe");
            return new Noop();
        }

        public Result Reload()
        {
            lock (order) order.Add("catalog.Reload");
            return reloadResult ?? Result.Success();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingRecovery(List<string> order, JobError? failWith = null) : ICrashRecovery
    {
        public Result<RecoveryReport, JobError> Recover(CancellationToken ct = default)
        {
            lock (order) order.Add("crashRecovery.Recover");
            if (failWith is not null)
                return failWith;
            return new RecoveryReport
            {
                JobsRecovered = 0, CompletedForward = 0, RolledBack = 0, CleanedPrePlacement = 0,
                QuarantinedPaths = [],
            };
        }
    }

    private sealed class RecordingIpcServer(List<string> order, string? failWith = null) : IIpcServer
    {
        public List<EngineEvent> Broadcasted { get; } = [];

        public Result Start()
        {
            lock (order) order.Add("ipcServer.Start");
            return failWith is null ? Result.Success() : failWith;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            lock (order) order.Add("ipcServer.StopAsync");
            return Task.CompletedTask;
        }

        public void Broadcast(EngineEvent evt) { lock (Broadcasted) Broadcasted.Add(evt); }
    }

    private sealed class RecordingOrchestrator(List<string> order) : IJobOrchestrator
    {
        public Result Start()
        {
            lock (order) order.Add("orchestrator.Start");
            return Result.Success();
        }

        public Task StopAsync()
        {
            lock (order) order.Add("orchestrator.StopAsync");
            return Task.CompletedTask;
        }

        public EngineStatusSnapshot GetStatus() => new(false, 0, 0, 0, null);
    }

    private sealed class RecordingEventBus(List<string> order) : IEngineEventBus
    {
        public List<EngineEvent> Published { get; } = [];

        public void Publish(EngineEvent evt) { lock (Published) Published.Add(evt); }

        public List<EngineEvent> Snapshot() { lock (Published) return [.. Published]; }

        public IDisposable Subscribe(Action<EngineEvent> handler)
        {
            lock (order) order.Add("eventBus.Subscribe");
            return new Noop();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingPauseState(List<string> order) : IPauseStateService
    {
        public bool IsPaused => false;
        public Result SetPaused(bool paused) => Result.Success();

        public IDisposable Subscribe(Action<bool> pauseHandler)
        {
            lock (order) order.Add("pauseState.Subscribe");
            return new Noop();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class StubAutostart : IAutostartRegistrar
    {
        public Result RegisterAutostart() => Result.Success();
        public Result UnregisterAutostart() => Result.Success();
    }

    private sealed class StubSettings(GlobalSettings? current = null) : ISettingsProvider
    {
        public GlobalSettings Current { get; } = current ?? GlobalSettings.Default;
        public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
            Result<GlobalSettings, string>.Success(settings);
    }

    // ---- harness ----------------------------------------------------------------------------------

    private sealed record Harness(
        EngineHost Host,
        RecordingLifetime Lifetime,
        RecordingIpcServer Server,
        RecordingEventBus Bus,
        EngineStartupState Startup);

    private Harness NewHost(
        Result? catalogReload = null,
        JobError? recoveryError = null,
        string? ipcStartError = null,
        GlobalSettings? settings = null,
        string? mutexName = null)
    {
        RecordingLifetime lifetime = new();
        RecordingIpcServer server = new(_order, ipcStartError);
        RecordingEventBus bus = new(_order);
        EngineStartupState startup = new();
        EngineHost host = new(
            NullLogger<EngineHost>.Instance,
            lifetime,
            new EnginePaths { Root = Path.Combine(_root, "engine") },
            new RecordingCatalog(_order, catalogReload),
            server,
            new StubSettings(settings),
            new StubAutostart(),
            new RecordingRecovery(_order, recoveryError),
            new RecordingOrchestrator(_order),
            bus,
            new RecordingPauseState(_order),
            startup,
            TimeProvider.System,
            mutexName ?? _mutexName);
        return new Harness(host, lifetime, server, bus, startup);
    }

    [Fact]
    public async Task The_startup_sequence_runs_in_the_documented_order()
    {
        Harness h = NewHost();

        await h.Host.StartAsync(CancellationToken.None);

        Assert.Equal(
            [
                "catalog.Reload",           // step 3
                "crashRecovery.Recover",    // step 4 — I-RECOVER-FIRST: before the server accepts anything
                "ipcServer.Start",          // step 5
                "eventBus.Subscribe",       // step 7 — bridges, and they must stay BELOW catalog.Reload
                "pauseState.Subscribe",
                "catalog.Subscribe",
                "orchestrator.Start",       // last: its first job's events are already observed
            ],
            await SettledOrderAsync(7));

        await h.Host.StopAsync(CancellationToken.None);
        h.Host.Dispose();
    }

    [Fact]
    public async Task A_recovery_failure_stops_the_application_without_starting_anything()
    {
        // A journal read failure must not silently start the engine as if all was well: a new job could
        // take path locks over files recovery was about to restore.
        Harness h = NewHost(recoveryError: new JobError { Code = JobErrorCode.JournalWriteFailed, Message = "journal unreadable" });

        await h.Host.StartAsync(CancellationToken.None);
        Assert.True(await WaitForAsync(() => h.Lifetime.StopCalls == 1), "the host must stop the application");

        List<string> order = await SettledOrderAsync(2);
        Assert.DoesNotContain("ipcServer.Start", order);
        Assert.DoesNotContain("orchestrator.Start", order);

        await h.Host.StopAsync(CancellationToken.None);
        h.Host.Dispose();
    }

    [Fact]
    public async Task An_ipc_start_failure_stops_the_application_without_starting_the_pipeline()
    {
        Harness h = NewHost(ipcStartError: "the pipe name is taken");

        await h.Host.StartAsync(CancellationToken.None);
        Assert.True(await WaitForAsync(() => h.Lifetime.StopCalls == 1), "the host must stop the application");

        List<string> order = await SettledOrderAsync(3);
        Assert.Contains("ipcServer.Start", order);
        Assert.DoesNotContain("orchestrator.Start", order);

        await h.Host.StopAsync(CancellationToken.None);
        h.Host.Dispose();
    }

    [Fact]
    public async Task Shutdown_drains_the_pipeline_before_stopping_ipc()
    {
        // I-ATOMIC-JOB: stop dequeuing and await in-flight jobs, and only then tear IPC down — the
        // other order would drop a finished job's completion event.
        Harness h = NewHost();
        await h.Host.StartAsync(CancellationToken.None);
        await SettledOrderAsync(7);
        lock (_order) _order.Clear();

        await h.Host.StopAsync(CancellationToken.None);

        Assert.Equal(["orchestrator.StopAsync", "ipcServer.StopAsync"], await SettledOrderAsync(2));
        h.Host.Dispose();
    }

    // NOT covered here, deliberately: step 1's single-instance guard. A named Mutex is re-entrant for
    // the thread that owns it, and both hosts' WaitOne would run on the same thread pool, so two hosts
    // in ONE process cannot exercise a guard whose whole purpose is cross-process. The instanceMutexName
    // seam exists so these tests never contend with a real service on the developer's machine, not to
    // make that guard testable in-process; verifying it needs two FileManager.Service.exe launches.

    [Fact]
    public async Task A_failed_profile_load_is_published_as_an_engine_warning()
    {
        // Otherwise: empty profile list, no banner, a "healthy" engine reporting ActiveProfiles 0, and
        // every trigger dropped at Information level — indistinguishable from "you have no profiles".
        Harness h = NewHost(catalogReload: "could not enumerate profiles: access denied");

        await h.Host.StartAsync(CancellationToken.None);
        Assert.True(await WaitForAsync(() => h.Bus.Snapshot().OfType<EngineWarningEvent>().Any()));

        EngineWarningEvent warning = Assert.Single(h.Bus.Snapshot().OfType<EngineWarningEvent>());
        Assert.Contains("Profiles could not be loaded", warning.Message);
        Assert.Contains("access denied", warning.Message);
        // ...and recorded, which is the copy that actually reaches a user: the event above is published
        // before a UI that launched this service has finished subscribing, so only the status snapshot
        // still has it by the time anyone can look.
        Assert.Equal(warning.Message, h.Startup.Warning);

        await h.Host.StopAsync(CancellationToken.None);
        h.Host.Dispose();
    }

    [Fact]
    public async Task A_configured_profiles_directory_that_does_not_exist_is_published_as_an_engine_warning()
    {
        // The motivating case a reload error does NOT cover: ProfileStore.LoadAll returns SUCCESS with
        // zero profiles when the directory is simply absent — a relocated ProfilesDirectory on a share
        // that is not mounted yet at logon, which is when the service autostarts. The user would
        // otherwise re-create their profiles and end up with duplicates once the share came back.
        GlobalSettings relocated = GlobalSettings.Default with
        {
            ProfilesDirectory = Path.Combine(_root, "not-mounted", "profiles"),
        };
        Harness h = NewHost(settings: relocated);

        await h.Host.StartAsync(CancellationToken.None);
        Assert.True(await WaitForAsync(() => h.Bus.Snapshot().OfType<EngineWarningEvent>().Any()));

        EngineWarningEvent warning = Assert.Single(h.Bus.Snapshot().OfType<EngineWarningEvent>());
        Assert.Contains("is not available", warning.Message);
        Assert.Contains("do not re-create your profiles yet", warning.Message);
        Assert.Equal(warning.Message, h.Startup.Warning);

        await h.Host.StopAsync(CancellationToken.None);
        h.Host.Dispose();
    }

    [Fact]
    public async Task A_clean_startup_publishes_no_warning()
    {
        Harness h = NewHost();

        await h.Host.StartAsync(CancellationToken.None);
        await SettledOrderAsync(7);

        Assert.Empty(h.Bus.Snapshot().OfType<EngineWarningEvent>());
        Assert.Null(h.Startup.Warning);

        await h.Host.StopAsync(CancellationToken.None);
        h.Host.Dispose();
    }
}
