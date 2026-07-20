using FileManager.Contracts.Profiles;

namespace FileManager.Contracts.Settings;

/// <summary>Machine-level settings edited in the UI and persisted by the service (settings.json).
/// These are NOT per-profile.</summary>
public sealed record GlobalSettings
{
    /// <summary>Independent of <see cref="Profile.SchemaVersion"/>; versions this settings file only.
    /// v2 replaced the dry-run concurrency scalars with <see cref="ScanThreading"/>.</summary>
    public int SchemaVersion { get; init; } = 2;

    /// <summary>When the worker service is started and stopped relative to the UI. Defaults to
    /// <see cref="Settings.ServiceStartupMode.StartAndStopWithProgram"/> so the service does not
    /// outlive the UI unless the user opts into a longer-lived mode.</summary>
    public ServiceStartupMode ServiceStartupMode { get; init; } = ServiceStartupMode.StartAndStopWithProgram;

    private readonly ScanThreadingSettings? _scanThreading;

    /// <summary>Machine-level scan (enumeration) and hash (evaluation) concurrency, including the
    /// per-drive budget hierarchy. Profiles no longer override concurrency; this is the single source.
    /// Never null: a settings.json predating this field deserializes with no value (the source
    /// generator does not run property initializers for absent members), which the getter reads back
    /// as <see cref="ScanThreadingSettings.Default"/>. The setter collapses an explicit default back to
    /// the absent (null) representation so that a settings object that omits the field and one that sets
    /// it to the default compare equal under the record's value-equality.</summary>
    public ScanThreadingSettings ScanThreading
    {
        get => _scanThreading ?? ScanThreadingSettings.Default;
        init => _scanThreading = value == ScanThreadingSettings.Default ? null : value;
    }

    /// <summary>The UI theme. Defaults to <see cref="Settings.ThemeMode.System"/> so the app follows the
    /// OS light/dark preference unless the user picks a fixed theme.</summary>
    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;

    public static GlobalSettings Default { get; } = new();
}
