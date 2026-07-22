using FileManager.Contracts;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Scanning;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
            ScanThreadingSettings st = normalized.ScanThreading;
            _logger.LogInformation(
                "Saved global settings (scan threads {Scan}, hash threads {Hash}, per-drive default {PerDrive})",
                Describe(st.MaxScanThreads), Describe(st.MaxHashThreads), Describe(st.PerDriveDefault));
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

            byte[] bytes = File.ReadAllBytes(file);
            GlobalSettings? loaded = JsonSerializer.Deserialize(bytes, FileManagerJsonContext.Default.GlobalSettings);
            if (loaded is null)
            {
                _logger.LogWarning("Settings file {File} deserialized to null; using defaults", file);
                return GlobalSettings.Default;
            }
            return Normalize(MigrateLegacy(loaded, bytes));
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

    // Pre-v2 settings.json had scalar dry-run concurrency (DryRunConcurrencyMode / DryRunManualWorkers)
    // instead of ScanThreading. Those members no longer bind, so without this a pinned worker count is
    // silently dropped. A persisted DryRunManualWorkers was only ever written in Manual mode (the old
    // Normalize dropped it otherwise), so its presence as a positive integer is a Manual pin — carry it
    // into the evaluation (hash) phase, which that scalar most directly governed. Idempotent: it
    // re-derives the same value on every load until the next Update rewrites the file in v2 shape.
    private GlobalSettings MigrateLegacy(GlobalSettings loaded, byte[] rawJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("ScanThreading", out _))
                return loaded;   // not an object, or already v2-shaped — nothing to migrate
            if (root.TryGetProperty("DryRunManualWorkers", out JsonElement workers)
                && workers.ValueKind == JsonValueKind.Number
                && workers.TryGetInt32(out int n) && n >= 1)
            {
                _logger.LogInformation(
                    "Migrating legacy dry-run manual worker pin ({Workers}) to scan-threading MaxHashThreads", n);
                return loaded with
                {
                    SchemaVersion = 2,
                    ScanThreading = loaded.ScanThreading with { MaxHashThreads = ThreadBudget.Explicit(n) },
                };
            }
            return loaded;
        }
        catch (JsonException)
        {
            // The typed load already succeeded; a malformed legacy detail just means no migration.
            return loaded;
        }
    }

    // An explicit thread count below 1 is meaningless (it would make a degree-of-parallelism throw),
    // so clamp every explicit budget to >= 1; auto budgets are left alone. Stored files and incoming
    // IPC values both pass through Load/Update. Specific-drive keys are canonicalized to match the
    // volume-key form the scheduler resolves, and blank keys are dropped. Directory settings must be
    // absolute: an empty or relative value (a hand-edited file, or a plain update-settings that
    // bypassed the relocate handler's validation) would silently empty the profile catalog, so it
    // falls back to the default instead of persisting.
    private GlobalSettings Normalize(GlobalSettings settings) =>
        settings with
        {
            ScanThreading = NormalizeThreading(settings.ScanThreading),
            ScratchDirectory = NormalizeDirectory(settings.ScratchDirectory, GlobalSettings.DefaultScratchDirectory, "ScratchDirectory"),
            ProfilesDirectory = NormalizeDirectory(settings.ProfilesDirectory, GlobalSettings.DefaultProfilesDirectory, "ProfilesDirectory"),
        };

    private string NormalizeDirectory(string value, string fallback, string name)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0 || !Path.IsPathFullyQualified(trimmed))
        {
            _logger.LogWarning(
                "Settings {Setting} value \"{Value}\" is not an absolute path; using the default {Default}",
                name, value, fallback);
            return fallback;
        }
        return trimmed;
    }

    private static ScanThreadingSettings NormalizeThreading(ScanThreadingSettings s)
    {
        Dictionary<DriveClass, ThreadBudget> byType = [];
        foreach (KeyValuePair<DriveClass, ThreadBudget> e in s.DriveTypeOverrides)
            byType[e.Key] = ClampBudget(e.Value);

        Dictionary<string, ThreadBudget> specific = [];
        foreach (KeyValuePair<string, ThreadBudget> e in s.SpecificDriveOverrides)
        {
            string key = ScanThreadResolver.NormalizeKey(e.Key);
            if (key.Length != 0)
                specific[key] = ClampBudget(e.Value);
        }

        return s with
        {
            MaxScanThreads = ClampBudget(s.MaxScanThreads),
            MaxHashThreads = ClampBudget(s.MaxHashThreads),
            PerDriveDefault = ClampBudget(s.PerDriveDefault),
            DriveTypeOverrides = byType,
            SpecificDriveOverrides = specific,
        };
    }

    private static ThreadBudget ClampBudget(ThreadBudget budget) =>
        budget.Value is int v ? ThreadBudget.Explicit(Math.Max(1, v)) : ThreadBudget.Auto;

    private static string Describe(ThreadBudget budget) => budget.IsAuto ? "auto" : budget.Value!.Value.ToString();
}
