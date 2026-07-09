using System;
using System.IO;

namespace FileManager.Core;

/// <summary>The engine's on-disk root (§9). Injected so tests can point everything at a temp
/// directory. Default: %LOCALAPPDATA%\FileManager\ (per-user, no elevation — spec §5.3).</summary>
public sealed record EnginePaths
{
    public required string Root { get; init; }

    public string ProfilesDirectory => Path.Combine(Root, "profiles");

    /// <summary>Rotating logs live here (spec §9): service-YYYYMMDD.log, 14-day retention.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Machine-level settings (§9): a single settings.json at the root.</summary>
    public string SettingsFilePath => Path.Combine(Root, "settings.json");

    public static EnginePaths Default() => new()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileManager"),
    };
}
