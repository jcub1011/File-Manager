using System;
using System.IO;

namespace FileManager.UI.Services;

/// <summary>The UI can only reference Contracts (§1 rule 3), so it cannot use
/// <c>FileManager.Core.EnginePaths</c>. This mirrors <c>EnginePaths.Default()</c>
/// (%LOCALAPPDATA%\FileManager\...) — keep the two in sync.</summary>
internal static class UiPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileManager");

    public static string LogsDirectory { get; } = Path.Combine(Root, "logs");

    /// <summary>Machine-level settings, written by the service. Mirrors
    /// <c>EnginePaths.Default().SettingsFilePath</c>; read at startup to apply the persisted theme
    /// before the first paint (avoids the IPC-latency theme flash).</summary>
    public static string SettingsFilePath { get; } = Path.Combine(Root, "settings.json");

    /// <summary>Client-side UI layout state (sidebar collapsed/width). Owned entirely by the UI —
    /// it never touches the service's <c>GlobalSettings</c> — and read/written via
    /// <see cref="UiStateStore"/>.</summary>
    public static string UiStateFilePath { get; } = Path.Combine(Root, "ui-state.json");
}
