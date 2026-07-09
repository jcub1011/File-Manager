using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Runtime.Versioning;

namespace FileManager.Platform.Windows;

/// <summary>Registers the worker service under the per-user HKCU Run key so Windows launches it at
/// this user's login (ServiceStartupMode.RunOnStartup). Per-user, so no elevation is needed — it
/// matches the per-user pipe/mutex model. Both operations are idempotent.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutostartRegistrar(ILogger<WindowsAutostartRegistrar> logger) : IAutostartRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileManager.Service";

    public Result RegisterAutostart()
    {
        // Assumes a self-contained / single-file deployment where ProcessPath IS the service exe.
        // If the service were ever launched via `dotnet FileManager.Service.dll`, ProcessPath would
        // be the dotnet host — the Run entry would then start the host with no dll argument. Revisit
        // this if the deployment model changes to framework-dependent.
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return "could not determine the service executable path for autostart registration";

        // Quote the path so a space in the directory does not break the login command line.
        string command = $"\"{exePath}\"";
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            object? existing = key.GetValue(ValueName);
            if (existing is string current && string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
                return Result.Success();        // already registered with the same path — idempotent no-op
            key.SetValue(ValueName, command, RegistryValueKind.String);
            logger.LogInformation("Registered service autostart at HKCU\\{Key}\\{Value}", RunKeyPath, ValueName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            // Last resort: registry failures become traceable failure values, not faulted callers.
            logger.LogError(ex, "Registering service autostart failed");
            return $"registering service autostart failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public Result UnregisterAutostart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null || key.GetValue(ValueName) is null)
                return Result.Success();        // nothing registered — idempotent no-op
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.LogInformation("Removed service autostart from HKCU\\{Key}\\{Value}", RunKeyPath, ValueName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            // Last resort: registry failures become traceable failure values, not faulted callers.
            logger.LogError(ex, "Removing service autostart failed");
            return $"removing service autostart failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
