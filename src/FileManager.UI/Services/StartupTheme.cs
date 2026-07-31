using FileManager.Contracts.Settings;

namespace FileManager.UI.Services;

/// <summary>The persisted <see cref="ThemeMode"/>, read before the main window is shown so the correct
/// theme is applied on the first paint. The theme is client-side state (nothing in the engine reads
/// it), so this is a plain local file read with no service round-trip — which also means the app comes
/// up in the right theme when the service is unreachable. Any missing/corrupt file falls back to
/// <see cref="ThemeMode.System"/> inside <see cref="ClientSettingsStore"/>.</summary>
internal static class StartupTheme
{
    public static ThemeMode Read() => ClientSettingsStore.Read().ThemeMode;

    internal static ThemeMode Read(string clientSettingsPath) =>
        ClientSettingsStore.Read(clientSettingsPath).ThemeMode;
}
