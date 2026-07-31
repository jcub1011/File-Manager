using FileManager.Contracts.Settings;
using System.Text.Json.Serialization;

namespace FileManager.UI.Services;

/// <summary>Settings owned entirely by this UI process. The service never reads them, which is the
/// whole point: unlike <c>GlobalSettings</c> — which the service owns, persists, and which the
/// settings window refuses to write when a load failed — these can always be written, including
/// while the service is unreachable. That is exactly when
/// <see cref="ServiceExecutablePath"/> matters. Persisted by <see cref="ClientSettingsStore"/>.</summary>
/// <param name="ServiceExecutablePath">Absolute path to FileManager.Service.exe, or null to use the
/// default resolution (see <c>FileManager.Contracts.IPC.ServiceLauncher</c>).</param>
/// <param name="ThemeMode">The application theme. Client-side because nothing in the engine reads it.</param>
/// <param name="SidebarCollapsed">Whether the profile sidebar is showing the narrow icon rail.</param>
/// <param name="SidebarWidth">The expanded sidebar width in device-independent pixels.</param>
public sealed record ClientSettings(
    string? ServiceExecutablePath,
    ThemeMode ThemeMode,
    bool SidebarCollapsed,
    double SidebarWidth)
{
    /// <summary>Defaults for a first run (or an unreadable file): no override (probe beside the app),
    /// follow the OS theme, sidebar expanded at the original hard-coded 280px panel width.</summary>
    public static ClientSettings Default { get; } = new(null, ThemeMode.System, false, 280);
}

/// <remarks><c>UseStringEnumConverter</c> matches <c>FileManagerJsonContext</c> so
/// <see cref="ThemeMode"/> reads and writes as "Dark", not 2 — both for a hand-editable file and so
/// the value migrated out of settings.json round-trips in the same shape it arrived in.</remarks>
[JsonSourceGenerationOptions(WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ClientSettings))]
public sealed partial class UiJsonContext : JsonSerializerContext;
