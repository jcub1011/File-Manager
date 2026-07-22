using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using Microsoft.Extensions.Logging;

namespace FileManager.Core.Platform;

/// <summary>Reconciles the OS autostart registration with the configured <see cref="ServiceStartupMode"/>:
/// registered iff the mode is <see cref="ServiceStartupMode.RunOnStartup"/>. Both operations are
/// idempotent, so this is safe to call at service startup and again on every settings change.</summary>
public static class AutostartApplier
{
    public static void Apply(ServiceStartupMode mode, IAutostartRegistrar registrar, ILogger logger)
    {
        Result result = mode == ServiceStartupMode.RunOnStartup
            ? registrar.RegisterAutostart()
            : registrar.UnregisterAutostart();

        if (result.TryGetError(out string? error))
            logger.LogWarning("Applying autostart for mode {Mode} failed: {Error}", mode, error);
    }
}
