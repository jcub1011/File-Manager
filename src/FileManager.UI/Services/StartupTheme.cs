using FileManager.Contracts;
using FileManager.Contracts.Settings;
using System;
using System.IO;
using System.Text.Json;

namespace FileManager.UI.Services;

/// <summary>Reads the persisted <see cref="ThemeMode"/> straight from settings.json on disk, so the
/// correct theme can be applied before the main window is shown. The authoritative copy lives with
/// the worker service and is normally fetched over IPC, but that round-trip (which may launch the
/// service) is far too slow for the first paint — reading the file directly is sub-millisecond and
/// needs no running service. Mirrors the read half of <c>Core.Settings.SettingsService.Load</c>;
/// any missing/corrupt file falls back to <see cref="ThemeMode.System"/> (the app default), and the
/// service's later IPC value reconciles anything that has since changed.</summary>
internal static class StartupTheme
{
    public static ThemeMode Read() => Read(UiPaths.SettingsFilePath);

    internal static ThemeMode Read(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath))
                return ThemeMode.System;

            using FileStream stream = File.OpenRead(settingsFilePath);
            GlobalSettings? settings = JsonSerializer.Deserialize(
                stream, FileManagerJsonContext.Default.GlobalSettings);
            return settings?.ThemeMode ?? ThemeMode.System;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not read startup theme from {File}; using System", settingsFilePath);
            return ThemeMode.System;
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception must not block startup — fall back to System.
            Serilog.Log.Error(ex, "Reading startup theme from {File} failed unexpectedly; using System", settingsFilePath);
            return ThemeMode.System;
        }
    }
}
