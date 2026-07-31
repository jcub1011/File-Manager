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

    /// <summary>Machine-level engine settings, written by the service. Mirrors
    /// <c>EnginePaths.Default().SettingsFilePath</c>. The UI no longer reads this file directly — the
    /// theme it used to source from here now lives in <see cref="ClientSettingsFilePath"/> — apart from
    /// the one-time migration probe in <see cref="ClientSettingsStore"/>.</summary>
    public static string SettingsFilePath { get; } = Path.Combine(Root, SettingsFileName);

    internal const string SettingsFileName = "settings.json";

    /// <summary>Settings owned entirely by the UI: the service executable path, the theme, and the
    /// sidebar layout. Deliberately separate from <see cref="SettingsFilePath"/>, which the service
    /// owns and rewrites, and which cannot be saved while the service is unreachable.</summary>
    public static string ClientSettingsFilePath { get; } = Path.Combine(Root, "client-settings.json");

    /// <summary>Pre-split client-side layout state, superseded by <see cref="ClientSettingsFilePath"/>.
    /// Read once by <see cref="ClientSettingsStore"/>'s migration and never written again.</summary>
    internal const string LegacyUiStateFileName = "ui-state.json";
}
