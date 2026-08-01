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
/// IMPORTANT — every write goes through <see cref="Update(string, Func{ClientSettings, ClientSettings})"/>,
/// which is read-modify-write under a lock. Two unrelated features share this file and save on
/// completely different triggers (the sidebar persists on every layout change, the settings window on
/// Save), so a writer that constructed a fresh record — which is what the predecessor
/// <c>UiStateStore</c> did, safely, because it owned its file alone — would silently erase whichever
/// half it does not know about, and two unsynchronised read-modify-writes could interleave and do the
/// same thing to each other.</summary>
internal static class ClientSettingsStore
{
    /// <summary>Serialises the read-modify-write pairs against each other. Process-wide is enough: this
    /// file belongs to the UI process alone — the service never reads or writes it, which is the whole
    /// reason it exists.</summary>
    private static readonly object Gate = new();

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

    /// <summary>Applies <paramref name="edit"/> to the CURRENT stored settings and persists the result,
    /// returning what was written. The only correct way to change one half of this file: reading and
    /// writing as separate steps lets the other writer's save land in between and be overwritten.
    /// <para><paramref name="edit"/> runs under the lock, so it must not block or call back in here.</para>
    /// </summary>
    internal static ClientSettings Update(string path, Func<ClientSettings, ClientSettings> edit)
    {
        lock (Gate)
        {
            ClientSettings updated = edit(Read(path));
            Write(path, updated);
            return updated;
        }
    }

    internal static void Write(string path, ClientSettings settings)
    {
        // Write-then-rename, never in place. This file now holds the service executable path — the
        // user's only way out of an unreachable service — so a write torn by a crash or a full disk
        // would replace a working override with half a JSON document, and the app would come up unable
        // to start its service and with no record of where it had been pointed. A rename is atomic on
        // NTFS, so a reader sees either the whole old file or the whole new one.
        string temp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (FileStream stream = File.Create(temp))
                JsonSerializer.Serialize(stream, settings, UiJsonContext.Default.ClientSettings);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A failed write costs a preference, never correctness; it must not disrupt the app.
            Serilog.Log.Warning(ex, "Could not write client settings to {File}", path);
            TryDeleteTemp(temp);
        }
    }

    /// <summary>Clears a half-written temp file so a failure does not leave litter beside the real one.
    /// Best-effort by nature — the write already failed and was already reported.</summary>
    private static void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Could not remove the partial client-settings file {File}", temp);
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
