using System;
using System.IO;

namespace FileManager.Core;

/// <summary>The engine's on-disk root (§9). Injected so tests can point everything at a temp
/// directory. Default: %LOCALAPPDATA%\FileManager\ (per-user, no elevation — spec §5.3).</summary>
public sealed record EnginePaths
{
    public required string Root { get; init; }

    public string ProfilesDirectory => Path.Combine(Root, "profiles");

    public static EnginePaths Default() => new()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileManager"),
    };
}
