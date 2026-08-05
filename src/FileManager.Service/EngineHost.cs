using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.IPC;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Observability;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Service;

/// <summary>Runs the §2.4 startup sequence. Steps that belong to later slices are numbered
/// [slot] comments so the invariant ordering (notably I-RECOVER-FIRST) stays visible.</summary>
internal sealed class EngineHost(
    ILogger<EngineHost> logger,
    IHostApplicationLifetime lifetime,
    Core.EnginePaths paths,
    IProfileCatalog catalog,
    IIpcServer ipcServer,
    ISettingsProvider settings,
    IAutostartRegistrar autostart,
    ICrashRecovery crashRecovery,
    IJobOrchestrator orchestrator,
    // Nothing called IRunCoordinator.StopAsync before, though the interface declared it as the teardown
    // entry point: the run-profile handler this replaced owned a shutdown token and a Dispose that
    // cancelled it, and moving that work into the coordinator left the teardown unwired. So a detached plan
    // walked on past shutdown — the exact hung-shutdown bug that Dispose was written to fix — and worse,
    // the deletion pass observes that never-cancelled token and kept recycling destination files while the
    // process exited.
    Core.Runs.IRunCoordinator runs,
    IEngineEventBus eventBus,
    IPauseStateService pauseState,
    Core.EngineStartupState startupState,
    TimeProvider time,
    // Test seam ONLY, and deliberately the last parameter with a default so the DI activation in
    // EngineComposition is unchanged. The derived name is a frozen cross-process contract, so it stays
    // here rather than moving out; this exists so a test never contends with a real service running on
    // the developer's machine.
    string? instanceMutexName = null) : BackgroundService
{
    private Mutex? _singleInstanceMutex;
    private IDisposable? _eventBridge;
    private IDisposable? _pauseBridge;
    private IDisposable? _profilesBridge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            // Last resort: a startup or run crash must land in the service log, not depend on
            // host default unhandled-exception behavior.
            logger.LogCritical(ex, "Engine host failed unexpectedly; stopping the service");
            lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // 1. Single-instance guard: a second service process (e.g. two UIs racing
        //    ServiceLauncher) exits immediately and harmlessly.
        _singleInstanceMutex = new Mutex(
            initiallyOwned: false,
            name: instanceMutexName
                ?? @"Local\FileManager.Service." + IpcEndpoint.SanitizeUserName(Environment.UserName));
        if (!_singleInstanceMutex.WaitOne(TimeSpan.Zero))
        {
            logger.LogInformation("Another FileManager.Service instance is already running for this user; exiting");
            lifetime.StopApplication();
            return;
        }

        // 1a. The GC configuration this process actually resolved. NativeAOT has no runtimeconfig.json
        //     parser — GC knobs arrive from DOTNET_-prefixed environment variables and two ILC-embedded
        //     blobs — so "the csproj says X" is not evidence that X reached the shipped binary. This is
        //     the only in-process way to confirm it, and every GC-tuning claim about the service is
        //     unfalsifiable without it. One ~40-entry dictionary, once per process.
        LogGcConfiguration();

        // 2. On-disk layout (§9).
        Directory.CreateDirectory(paths.ProfilesDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        Directory.CreateDirectory(paths.JobLogsDirectory);
        Directory.CreateDirectory(paths.JournalDirectory);
        Directory.CreateDirectory(paths.AuditDirectory);
        Directory.CreateDirectory(paths.StateDirectory);
        Directory.CreateDirectory(paths.WorkDirectory);
        Directory.CreateDirectory(paths.QuarantineDirectory);

        // 2a. Dry-run scratch snapshots (spool spill-over): create the configured directory and purge
        //     any *.snapshot left by a crash. Each run deletes its own on completion, so this is only a
        //     backstop. Non-fatal — a scratch issue must not stop the service; the spool degrades to
        //     its in-memory fast path if it cannot write.
        PurgeScratchDirectory();

        // 2b. Run snapshots: sweep any directory a previous process left behind. A run drops its own when
        //     it closes, so anything here belongs to a run that died or to a cleanup that failed on a
        //     locked file — and nothing else ever enumerates this directory, so without this a leaked
        //     snapshot stays in %LOCALAPPDATA% permanently. Before anything can accept a run, so every id
        //     found is provably from a dead process.
        PurgeRunSnapshots();

        // 3. Load profiles into the catalog. A failure here is non-fatal — the service must still come
        //    up so the user can fix the cause from the UI — but it must not be SILENT: the catalog is
        //    left empty, list-profiles then answers successfully with zero profiles, and every trigger
        //    is dropped at Information level, so the UI is indistinguishable from "you have no
        //    profiles" on a healthy engine. The message is carried to step 7 and published there,
        //    because nothing can reach a client until the event bridge exists.
        string? profileLoadWarning = null;
        Result reload = catalog.Reload();
        if (reload.TryGetError(out string? reloadError))
        {
            logger.LogError("Initial profile load failed: {Error}", reloadError);
            profileLoadWarning =
                $"Profiles could not be loaded from {settings.Current.ProfilesDirectory}: {reloadError}. " +
                "The profile list is empty and no triggers will run until this is fixed.";
        }
        else if (!Directory.Exists(settings.Current.ProfilesDirectory))
        {
            // The motivating case, and the one a reload error does NOT cover: ProfileStore.LoadAll
            // returns SUCCESS with zero profiles when the directory is simply not there. A relocated
            // ProfilesDirectory on a network share is routinely unmounted at logon, and the service
            // autostarts from HKCU Run — so the user sees an empty list, re-creates their profiles, and
            // ends up with duplicates once the share comes back. Step 2 created <Root>\profiles, so this
            // can only fire for a directory the user configured somewhere else.
            logger.LogError(
                "Configured profiles directory {Directory} does not exist; the catalog is empty",
                settings.Current.ProfilesDirectory);
            profileLoadWarning =
                $"The configured profiles directory {settings.Current.ProfilesDirectory} is not available " +
                "(a disconnected drive or share, or a folder that was moved or removed). The profile list is " +
                "empty and no triggers will run until it is reachable — do not re-create your profiles yet.";
        }

        // 4. Crash recovery — resolves every OPEN journal entry to CLOSED. MUST run to completion
        //    here, before step 5 starts the IPC server and before any trigger fires
        //    (I-RECOVER-FIRST, §7.4).
        Result<RecoveryReport, Core.Jobs.JobError> recovery = crashRecovery.Recover(stoppingToken);
        if (recovery.TryGetValue(out RecoveryReport? report))
        {
            logger.LogInformation(
                "Recovery complete: {Recovered} recovered, {Forward} forward, {Back} rolled back, {Clean} pre-placement, {Quarantined} quarantined",
                report.JobsRecovered, report.CompletedForward, report.RolledBack, report.CleanedPrePlacement, report.QuarantinedPaths.Count);
            foreach (string quarantined in report.QuarantinedPaths)
                logger.LogWarning("Quarantined orphaned content: {Path}", quarantined);
        }
        else if (recovery.TryGetError(out Core.Jobs.JobError? recoveryError))
        {
            // A journal read failure must not silently start the engine as if all was well.
            logger.LogCritical("Crash recovery failed: {Error}; stopping the service", recoveryError.Message);
            lifetime.StopApplication();
            return;
        }

        // 5. Start the IPC server.
        Result started = ipcServer.Start();
        if (started.TryGetError(out string? startError))
        {
            logger.LogCritical("IPC server failed to start: {Error}", startError);
            lifetime.StopApplication();
            return;
        }

        // 6. Reconcile OS autostart with the configured startup mode (idempotent).
        AutostartApplier.Apply(settings.Current.ServiceStartupMode, autostart, logger);
        //    [slot] IShellIntegration.RegisterContextMenu().

        // 7. Start the live job pipeline (§2.4 step 6): bridge engine events to IPC subscribers,
        //    mirror pause changes and catalog reloads as events, then start the orchestrator's
        //    trigger-queue consumer.
        //    Order: bridges first so the consumer's very first job's events are already observed.
        //    These MUST stay below step 3's catalog.Reload() — creating the catalog bridge earlier
        //    would publish a spurious profiles-changed at startup. That ordering is load-bearing.
        _eventBridge = eventBus.Subscribe(ipcServer.Broadcast);
        _pauseBridge = pauseState.Subscribe(paused =>
            eventBus.Publish(new PauseChangedEvent { AtUtc = time.GetUtcNow(), Paused = paused }));
        // Fan-out runs synchronously on the thread that reloaded the catalog (an IPC handler), but
        // every hop from here to the wire is non-blocking (bus → Broadcast → drop-oldest channel),
        // so this is safe there. Note a subscriber can therefore observe profiles-changed BEFORE the
        // requesting client's own success response; clients must tolerate that.
        _profilesBridge = catalog.Subscribe(() =>
            eventBus.Publish(new ProfilesChangedEvent { AtUtc = time.GetUtcNow() }));

        // Step 3's failure. It is recorded on the startup state FIRST and published second, and the
        // recording is what actually reaches the user: this runs microseconds after step 5 opened the
        // IPC server, so a UI that launched this service has not finished subscribing yet and the event
        // is dropped. GetStatusHandler stamps the recorded copy onto every status snapshot, so whenever
        // a client connects it learns about this on its first poll. The event stays for the client that
        // is already attached (a second window, a session where the service was already up).
        if (profileLoadWarning is not null)
        {
            startupState.Warning = profileLoadWarning;
            eventBus.Publish(new EngineWarningEvent { AtUtc = time.GetUtcNow(), Message = profileLoadWarning });
        }

        Result orchestratorStarted = orchestrator.Start();
        if (orchestratorStarted.TryGetError(out string? orchestratorError))
        {
            logger.LogCritical("Job orchestrator failed to start: {Error}", orchestratorError);
            lifetime.StopApplication();
            return;
        }
        //    [slot] Start triggers: watcher, scheduler missed-run evaluation.
        //    [slot] Spawn the tray client when a desktop session exists.

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        // Drain the live pipeline before stopping IPC: stop dequeuing and await in-flight jobs
        // (I-ATOMIC-JOB — a started job is never suspended), then tear down the event bridges.
        //
        // Runs FIRST. Its StopAsync cancels the shutdown token that a detached plan's walk and the Mirror
        // deletion pass both observe, and awaits them — so a plan of a multi-million-file tree stops
        // instead of holding ScanScheduler's worker threads until the process dies, and the deletion pass
        // stops at an orphan boundary rather than being killed mid-move and leaving an mrdel with no
        // mrdone. Cancelling the runs also drops their queued payloads, which is less for the
        // orchestrator below to drain.
        await runs.StopAsync();
        await orchestrator.StopAsync();
        _profilesBridge?.Dispose();
        _pauseBridge?.Dispose();
        _eventBridge?.Dispose();
        await ipcServer.StopAsync(CancellationToken.None);
    }

    /// <summary>Logs the GC's resolved configuration once at startup, plus the server/concurrent flags
    /// read back from the runtime itself (the dictionary reports only knobs that were explicitly set, so
    /// an absent key means "default", which is exactly the ambiguity these two lines remove).</summary>
    private void LogGcConfiguration()
    {
        try
        {
            if (!logger.IsEnabled(LogLevel.Information))
                return;
            List<string> entries = [];
            foreach (KeyValuePair<string, object> variable in GC.GetConfigurationVariables())
                entries.Add($"{variable.Key}={variable.Value}");
            entries.Sort(StringComparer.Ordinal);
            // LatencyMode is the readable proxy for background GC: with concurrent GC off it reports
            // Batch, with it on (the default) Interactive. There is no direct IsConcurrentGC property.
            logger.LogInformation(
                "GC configuration: server={Server}, latency={Latency}, explicitly-set variables: {Variables}",
                GCSettings.IsServerGC, GCSettings.LatencyMode,
                entries.Count == 0 ? "(none)" : string.Join(", ", entries));
        }
        catch (Exception ex)
        {
            // Last resort: a diagnostic must never be able to stop the service from starting.
            logger.LogWarning(ex, "Could not read the GC configuration");
        }
    }

    private void PurgeScratchDirectory()
    {
        try
        {
            string scratch = settings.Current.ScratchDirectory;
            Directory.CreateDirectory(scratch);
            foreach (string leftover in Directory.EnumerateFiles(scratch, "*.snapshot"))
            {
                try { File.Delete(leftover); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not purge leftover dry-run snapshot {Path}", leftover);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prepare the dry-run scratch directory");
        }
    }

    /// <summary>Removes run snapshot directories left behind by a previous process.
    ///
    /// <para>A run deletes its own when it closes, so anything here belongs to a run that died mid-flight
    /// or to a cleanup that failed on a locked file. Neither has an owner any more: nothing else enumerates
    /// this directory, so without this sweep a leaked snapshot — plan.json plus four ndjsonl files, up to
    /// two rows per scanned file at the 500k cap — stays in the user's %LOCALAPPDATA% permanently. Three
    /// separate comments used to promise this sweep existed while <c>EnginePaths</c> said it did not.</para>
    ///
    /// <para>Safe because it runs before anything can accept a run: every id here is from a dead process.
    /// Non-fatal for the same reason the scratch purge is — a stale directory is untouched user-invisible
    /// scaffolding, never a reason to refuse to start.</para></summary>
    private void PurgeRunSnapshots()
    {
        try
        {
            if (!Directory.Exists(paths.RunsDirectory))
                return;
            foreach (string leftover in Directory.EnumerateDirectories(paths.RunsDirectory))
            {
                if (Core.Files.InfrastructurePaths.TryDeleteDirectory(leftover) is Exception ex)
                    logger.LogWarning(ex, "Could not purge leftover run snapshot {Path}", leftover);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not sweep the run snapshot directory");
        }
    }

    public override void Dispose()
    {
        _profilesBridge?.Dispose();
        _pauseBridge?.Dispose();
        _eventBridge?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.Dispose();
    }
}
