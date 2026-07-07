using System;
using System.IO;

namespace FileManager.UI.Services;

/// <summary>The UI can only reference Contracts (§1 rule 3), so it cannot use
/// <c>FileManager.Core.EnginePaths</c>. This mirrors <c>EnginePaths.Default().LogsDirectory</c>
/// (%LOCALAPPDATA%\FileManager\logs) — keep the two in sync.</summary>
internal static class UiPaths
{
    public static string LogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileManager",
        "logs");
}
