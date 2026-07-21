using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.IPC;
using FileManager.Core.Journal;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
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
    ICrashRecovery crashRecovery) : BackgroundService
{
    private Mutex? _singleInstanceMutex;

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
            name: @"Local\FileManager.Service." + IpcEndpoint.SanitizeUserName(Environment.UserName));
        if (!_singleInstanceMutex.WaitOne(TimeSpan.Zero))
        {
            logger.LogInformation("Another FileManager.Service instance is already running for this user; exiting");
            lifetime.StopApplication();
            return;
        }

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

        // 3. Load profiles into the catalog.
        Result reload = catalog.Reload();
        if (reload.TryGetError(out string? reloadError))
            logger.LogError("Initial profile load failed: {Error}", reloadError);

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
        //    [slot] Start triggers: watcher, scheduler missed-run evaluation, trigger-queue consumer.
        //    [slot] Spawn the tray client when a desktop session exists.

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        await ipcServer.StopAsync(CancellationToken.None);
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

    public override void Dispose()
    {
        _singleInstanceMutex?.Dispose();
        base.Dispose();
    }
}
