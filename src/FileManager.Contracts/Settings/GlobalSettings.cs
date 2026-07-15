using FileManager.Contracts.Profiles;

namespace FileManager.Contracts.Settings;

/// <summary>Machine-level settings edited in the UI and persisted by the service (settings.json).
/// These are NOT per-profile.</summary>
public sealed record GlobalSettings
{
    /// <summary>Independent of <see cref="Profile.SchemaVersion"/>; versions this settings file only.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>When the worker service is started and stopped relative to the UI. Defaults to
    /// <see cref="Settings.ServiceStartupMode.StartAndStopWithProgram"/> so the service does not
    /// outlive the UI unless the user opts into a longer-lived mode.</summary>
    public ServiceStartupMode ServiceStartupMode { get; init; } = ServiceStartupMode.StartAndStopWithProgram;

    /// <summary>Global default for dry-run evaluation concurrency. Only <see cref="ConcurrencyMode.Automatic"/>
    /// or <see cref="ConcurrencyMode.Manual"/> are meaningful here — Inherit is a per-profile-only mode.</summary>
    public ConcurrencyMode DryRunConcurrencyMode { get; init; } = ConcurrencyMode.Automatic;

    /// <summary>Worker count when <see cref="DryRunConcurrencyMode"/> is Manual; clamped to >= 1 by the
    /// service. Null / ignored when Automatic.</summary>
    public int? DryRunManualWorkers { get; init; }

    /// <summary>The UI theme. Defaults to <see cref="Settings.ThemeMode.System"/> so the app follows the
    /// OS light/dark preference unless the user picks a fixed theme.</summary>
    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;

    public static GlobalSettings Default { get; } = new();
}
