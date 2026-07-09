using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.IPC;
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
    IAutostartRegistrar autostart) : BackgroundService
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

        // 3. Load profiles into the catalog.
        Result reload = catalog.Reload();
        if (reload.TryGetError(out string? reloadError))
            logger.LogError("Initial profile load failed: {Error}", reloadError);

        // 4. [slot] ICrashRecovery.Recover() — when the journal lands, it MUST run to
        //    completion here, before step 5 starts the IPC server and before any trigger
        //    fires (I-RECOVER-FIRST, §7.4).

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

    public override void Dispose()
    {
        _singleInstanceMutex?.Dispose();
        base.Dispose();
    }
}
