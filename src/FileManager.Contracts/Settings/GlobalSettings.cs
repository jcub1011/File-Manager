using FileManager.Contracts.Profiles;
using System.IO;

namespace FileManager.Contracts.Settings;

/// <summary>Machine-level settings edited in the UI and persisted by the service (settings.json).
/// These are NOT per-profile.</summary>
public sealed record GlobalSettings
{
    /// <summary>Independent of <see cref="Profile.SchemaVersion"/>; versions this settings file only.
    /// v2 replaced the dry-run concurrency scalars with <see cref="ScanThreading"/>. v3 added
    /// <see cref="ScratchDirectory"/>.</summary>
    public int SchemaVersion { get; init; } = 3;

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

    private readonly string? _scratchDirectory;

    /// <summary>Where the service spills large dry-run findings as an append-only snapshot before
    /// streaming them to the UI (keeps service memory flat and decouples the scan from UI consumption;
    /// small runs stay in memory and never touch it). Never null: a settings.json predating this field
    /// deserializes with no value (the source generator does not run property initializers for absent
    /// members), which the getter reads back as <see cref="DefaultScratchDirectory"/> — a
    /// <c>scratch</c> folder under the process working directory. The setter collapses that same default
    /// back to the absent (null) representation so record value-equality is unaffected by the choice of
    /// representation.</summary>
    public string ScratchDirectory
    {
        get => _scratchDirectory ?? DefaultScratchDirectory;
        init => _scratchDirectory = value == DefaultScratchDirectory ? null : value;
    }

    /// <summary>The default scratch location: a <c>scratch</c> folder in the current working directory.
    /// Resolved on read so it reflects the process that loaded the settings (the service).</summary>
    public static string DefaultScratchDirectory => Path.Combine(Directory.GetCurrentDirectory(), "scratch");

    public static GlobalSettings Default { get; } = new();
}
