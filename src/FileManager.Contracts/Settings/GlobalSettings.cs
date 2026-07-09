using FileManager.Contracts.Profiles;

namespace FileManager.Contracts.Settings;

/// <summary>Machine-level settings edited in the UI and persisted by the service (settings.json).
/// These are NOT per-profile. Currently just the dry-run evaluation concurrency knob.</summary>
public sealed record GlobalSettings
{
    /// <summary>Independent of <see cref="Profile.SchemaVersion"/>; versions this settings file only.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Global default for dry-run evaluation concurrency. Only <see cref="ConcurrencyMode.Automatic"/>
    /// or <see cref="ConcurrencyMode.Manual"/> are meaningful here — Inherit is a per-profile-only mode.</summary>
    public ConcurrencyMode DryRunConcurrencyMode { get; init; } = ConcurrencyMode.Automatic;

    /// <summary>Worker count when <see cref="DryRunConcurrencyMode"/> is Manual; clamped to >= 1 by the
    /// service. Null / ignored when Automatic.</summary>
    public int? DryRunManualWorkers { get; init; }

    public static GlobalSettings Default { get; } = new();
}
