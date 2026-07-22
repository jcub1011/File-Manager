using FileManager.Contracts.Profiles;
using System;
using System.IO;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.Settings;

/// <summary>Machine-level settings edited in the UI and persisted by the service (settings.json).
/// These are NOT per-profile.</summary>
public sealed record GlobalSettings
{
    /// <summary>Independent of <see cref="Profile.SchemaVersion"/>; versions this settings file only.
    /// v2 replaced the dry-run concurrency scalars with <see cref="ScanThreading"/>. v3 added
    /// <see cref="ScratchDirectory"/>. v4 added <see cref="ProfilesDirectory"/>.</summary>
    public int SchemaVersion { get; init; } = 4;

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
    /// members), which the getter reads back as <see cref="DefaultScratchDirectory"/>. The setter
    /// collapses that same default back to the absent (null) representation so record value-equality
    /// is unaffected by the choice of representation. Serialization goes through
    /// <see cref="ScratchDirectorySerialized"/> so the absent representation survives a save.</summary>
    [JsonIgnore]
    public string ScratchDirectory
    {
        get => _scratchDirectory ?? DefaultScratchDirectory;
        init => _scratchDirectory = string.Equals(value, DefaultScratchDirectory, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>Serialization surface for <see cref="ScratchDirectory"/>: carries the nullable backing
    /// representation, so an unset (default) value stays ABSENT in settings.json and on the wire
    /// instead of pinning one machine's resolved default forever.</summary>
    [JsonInclude, JsonPropertyName("ScratchDirectory")]
    public string? ScratchDirectorySerialized
    {
        get => _scratchDirectory;
        init => _scratchDirectory = value is null ? _scratchDirectory : ScratchCollapse(value);
    }

    private static string? ScratchCollapse(string value) =>
        string.Equals(value, DefaultScratchDirectory, StringComparison.OrdinalIgnoreCase) ? null : value;

    /// <summary>The default scratch location: <c>%LOCALAPPDATA%\FileManager\scratch</c> — anchored
    /// beside the other engine paths, never the process working directory (which is Program Files or
    /// System32 in real launches, both unwritable/unstable). Resolved on read.</summary>
    public static string DefaultScratchDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileManager", "scratch");

    private readonly string? _profilesDirectory;

    /// <summary>Where profile *.json files live — user-selectable so profiles can be kept on a synced
    /// or shared folder. The service resolves its profile store against this on every read, so a change
    /// takes effect without a restart. Never null: a settings.json predating this field deserializes
    /// with no value (the source generator does not run property initializers for absent members),
    /// which the getter reads back as <see cref="DefaultProfilesDirectory"/>. The setter collapses that
    /// same default back to the absent (null) representation so record value-equality is unaffected by
    /// the choice of representation. Serialization goes through
    /// <see cref="ProfilesDirectorySerialized"/> so the absent representation survives a save.</summary>
    [JsonIgnore]
    public string ProfilesDirectory
    {
        get => _profilesDirectory ?? DefaultProfilesDirectory;
        init => _profilesDirectory = string.Equals(value, DefaultProfilesDirectory, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>Serialization surface for <see cref="ProfilesDirectory"/> (see
    /// <see cref="ScratchDirectorySerialized"/> for why).</summary>
    [JsonInclude, JsonPropertyName("ProfilesDirectory")]
    public string? ProfilesDirectorySerialized
    {
        get => _profilesDirectory;
        init => _profilesDirectory = value is null ? _profilesDirectory : ProfilesCollapse(value);
    }

    private static string? ProfilesCollapse(string value) =>
        string.Equals(value, DefaultProfilesDirectory, StringComparison.OrdinalIgnoreCase) ? null : value;

    /// <summary>The default profiles location: <c>%LOCALAPPDATA%\FileManager\profiles</c>. This MUST
    /// match <c>EnginePaths.Default().ProfilesDirectory</c> so an install predating this setting keeps
    /// finding its profiles with no migration.</summary>
    public static string DefaultProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileManager", "profiles");

    public static GlobalSettings Default { get; } = new();
}
