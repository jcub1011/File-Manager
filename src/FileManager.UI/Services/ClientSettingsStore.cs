using FileManager.Contracts.Settings;
using System;
using System.IO;
using System.Text.Json;

namespace FileManager.UI.Services;

/// <summary>Reads and writes the client-side <see cref="ClientSettings"/> to a small JSON file under
/// %LOCALAPPDATA%\FileManager. Mirrors the defensive read pattern of <see cref="StartupTheme"/>: any
/// missing/corrupt file or IO failure falls back to <see cref="ClientSettings.Default"/>, and a failed
/// write is logged but never throws.
///
/// IMPORTANT — every write must be read-modify-write:
/// <code>ClientSettingsStore.Write(path, ClientSettingsStore.Read(path) with { ThemeMode = value });</code>
/// Two unrelated features share this file and save on completely different triggers (the sidebar
/// persists on every layout change, the settings window on Save). Constructing a fresh record — which
/// is what the predecessor <c>UiStateStore</c> did, safely, because it owned its file alone — would
/// silently erase whichever half the writer does not know about.</summary>
internal static class ClientSettingsStore
{
    public static ClientSettings Read() => Read(UiPaths.ClientSettingsFilePath);

    internal static ClientSettings Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Migrate(path);

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, UiJsonContext.Default.ClientSettings)
                   ?? ClientSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not read client settings from {File}; using defaults", path);
            return ClientSettings.Default;
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception must not block startup — fall back to defaults.
            Serilog.Log.Error(ex, "Reading client settings from {File} failed unexpectedly; using defaults", path);
            return ClientSettings.Default;
        }
    }

    /// <summary>The provider handed to <see cref="IpcGateway"/>. Re-read per connect attempt (rather
    /// than snapshotted at startup) so a path corrected in the settings window takes effect on the very
    /// next attempt to reach the service, with no app restart.</summary>
    public static string? ReadServiceExecutablePath() => Read().ServiceExecutablePath;

    public static void Write(ClientSettings settings) => Write(UiPaths.ClientSettingsFilePath, settings);

    internal static void Write(string path, ClientSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, settings, UiJsonContext.Default.ClientSettings);
        }
        catch (Exception ex)
        {
            // A failed write costs a preference, never correctness; it must not disrupt the app.
            Serilog.Log.Warning(ex, "Could not write client settings to {File}", path);
        }
    }

    /// <summary>First run after the split: recover the values that used to live elsewhere rather than
    /// silently resetting them — the sidebar layout from ui-state.json, and the theme from the
    /// service's settings.json (where it was a <c>GlobalSettings</c> member before it became a client
    /// concern). Both are probed with <see cref="JsonDocument"/>: the old types are gone, and the
    /// current <c>GlobalSettings</c> no longer has a ThemeMode member to deserialize into. One-time by
    /// construction — the first <see cref="Write"/> makes the client file authoritative, and neither
    /// legacy file is ever written again.</summary>
    private static ClientSettings Migrate(string clientSettingsPath)
    {
        // Sibling files, so honour a test seam that redirects the client file out of %LOCALAPPDATA%.
        string? root = Path.GetDirectoryName(clientSettingsPath);
        if (string.IsNullOrEmpty(root))
            return ClientSettings.Default;

        ClientSettings migrated = ClientSettings.Default;

        if (ReadMember(Path.Combine(root, UiPaths.LegacyUiStateFileName), "SidebarCollapsed") is { } collapsed
            && collapsed.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            migrated = migrated with { SidebarCollapsed = collapsed.GetBoolean() };
        }
        if (ReadMember(Path.Combine(root, UiPaths.LegacyUiStateFileName), "SidebarWidth") is { } width
            && width.ValueKind is JsonValueKind.Number && width.TryGetDouble(out double px))
        {
            migrated = migrated with { SidebarWidth = px };
        }
        if (ReadMember(Path.Combine(root, UiPaths.SettingsFileName), "ThemeMode") is { } theme
            && theme.ValueKind is JsonValueKind.String
            && Enum.TryParse(theme.GetString(), ignoreCase: true, out ThemeMode mode))
        {
            migrated = migrated with { ThemeMode = mode };
        }

        if (migrated != ClientSettings.Default)
            Serilog.Log.Information("Migrated client settings from the pre-split files in {Root}", root);
        return migrated;
    }

    /// <summary>One top-level member of a JSON object file, or null if the file is missing, unreadable,
    /// malformed, or simply does not have it. A migration is best-effort by definition: the cost of
    /// failure is a preference reverting to its default, so nothing here may throw.</summary>
    private static JsonElement? ReadMember(string path, string name)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out JsonElement value)
                // The document is disposed on return, so hand back a detached copy.
                ? value.Clone()
                : null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Could not read {Member} from {File} while migrating client settings", name, path);
            return null;
        }
    }
}
