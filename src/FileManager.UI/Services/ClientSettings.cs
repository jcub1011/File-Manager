using FileManager.Contracts.Settings;
using System;
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
/// <param name="PreviewStaleAfterMinutes">How old a retained preview may get before it is flagged as
/// possibly out of date. <b>Nullable, and absent means the default</b> — this is a positional record, so
/// System.Text.Json deserializes it through the constructor and an absent member arrives as
/// <c>default(int)</c>, i.e. 0, not the intended 15. Read it through
/// <see cref="PreviewStaleAfter"/> rather than directly. (<paramref name="SidebarWidth"/> has the same
/// hazard and is clamped on read by its consumer for the same reason.)
/// <para>Client-side per the §2.3 ownership rule: the engine never reads it. A stale preview is purely a
/// statement about what the USER is looking at — the plan itself is exactly as valid as when it was
/// frozen, and the engine will execute it faithfully whenever it is approved.</para></param>
public sealed record ClientSettings(
    string? ServiceExecutablePath,
    ThemeMode ThemeMode,
    bool SidebarCollapsed,
    double SidebarWidth,
    int? PreviewStaleAfterMinutes = null)
{
    /// <summary>How long a preview stays fresh when the setting is absent. Fifteen minutes is chosen to be
    /// longer than a user takes to read a plan and shorter than a folder takes to drift materially.</summary>
    public const int DefaultPreviewStaleAfterMinutes = 15;

    /// <summary>The staleness threshold, with the absent-means-default and out-of-range cases resolved. A
    /// non-positive stored value reads as the default rather than as "always stale", which is what a
    /// hand-edited 0 would otherwise mean.</summary>
    [JsonIgnore]
    public TimeSpan PreviewStaleAfter => TimeSpan.FromMinutes(
        PreviewStaleAfterMinutes is > 0 ? PreviewStaleAfterMinutes.Value : DefaultPreviewStaleAfterMinutes);

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
