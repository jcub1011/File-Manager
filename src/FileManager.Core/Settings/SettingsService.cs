using FileManager.Contracts;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace FileManager.Core.Settings;

/// <summary>Reads and writes the single machine-level settings.json (§9). There is no config-file
/// binding framework (AOT constraint), so this owns load/persist directly.</summary>
public interface ISettingsProvider
{
    /// <summary>The live, process-wide settings snapshot. Read by the dry-run engine per run so a
    /// UI change takes effect on the next simulation without a restart.</summary>
    GlobalSettings Current { get; }

    /// <summary>Clamps, atomically persists, then swaps <see cref="Current"/>. Returns the stored
    /// (normalized) settings so callers reflect any clamping.</summary>
    Result<GlobalSettings, string> Update(GlobalSettings settings);
}

/// <summary>Loads settings.json once at construction (missing or corrupt → <see cref="GlobalSettings.Default"/>,
/// logged); <see cref="Update"/> writes atomically (temp + fsync + rename, mirroring
/// <see cref="Profiles.ProfileStore"/>) then swaps the in-memory snapshot. Registered as a singleton.</summary>
public sealed class SettingsService : ISettingsProvider
{
    private readonly ILogger<SettingsService> _logger;
    private readonly EnginePaths _paths;
    private volatile GlobalSettings _current;

    public SettingsService(ILogger<SettingsService> logger, EnginePaths paths)
    {
        _logger = logger;
        _paths = paths;
        _current = Load();
    }

    public GlobalSettings Current => _current;

    public Result<GlobalSettings, string> Update(GlobalSettings settings)
    {
        GlobalSettings normalized = Normalize(settings);
        try
        {
            Directory.CreateDirectory(_paths.Root);
            string finalPath = _paths.SettingsFilePath;
            string tempPath = finalPath + ".tmp";

            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, normalized, FileManagerJsonContext.Default.GlobalSettings);
                stream.Flush(flushToDisk: true);   // §9 fsync point: flush before the rename
            }
            File.Move(tempPath, finalPath, overwrite: true);

            _current = normalized;
            _logger.LogInformation("Saved global settings (dry-run concurrency {Mode}/{Workers})",
                normalized.DryRunConcurrencyMode, normalized.DryRunManualWorkers);
            return Result<GlobalSettings, string>.Success(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save global settings");
            return $"could not save settings: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            _logger.LogError(ex, "Saving global settings failed unexpectedly");
            return $"could not save settings: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private GlobalSettings Load()
    {
        string file = _paths.SettingsFilePath;
        try
        {
            if (!File.Exists(file))
                return GlobalSettings.Default;

            using FileStream stream = File.OpenRead(file);
            GlobalSettings? loaded = JsonSerializer.Deserialize(stream, FileManagerJsonContext.Default.GlobalSettings);
            if (loaded is null)
            {
                _logger.LogWarning("Settings file {File} deserialized to null; using defaults", file);
                return GlobalSettings.Default;
            }
            return Normalize(loaded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file {File} could not be loaded; using defaults", file);
            return GlobalSettings.Default;
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception falls back to defaults rather than faulting startup.
            _logger.LogError(ex, "Loading settings file {File} failed unexpectedly; using defaults", file);
            return GlobalSettings.Default;
        }
    }

    // A Manual worker count below 1 is meaningless and would make MaxDegreeOfParallelism throw, so
    // clamp it here — stored files and incoming IPC values both pass through Load/Update.
    private static GlobalSettings Normalize(GlobalSettings settings) =>
        settings.DryRunConcurrencyMode == ConcurrencyMode.Manual
            ? settings with { DryRunManualWorkers = Math.Max(1, settings.DryRunManualWorkers ?? 1) }
            : settings;
}
