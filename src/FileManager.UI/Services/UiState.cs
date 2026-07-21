using System.Text.Json.Serialization;

namespace FileManager.UI.Services;

/// <summary>Persisted, client-side UI layout preferences. Kept out of the service's
/// <c>GlobalSettings</c> because it is purely a UI concern. Persisted by <see cref="UiStateStore"/>.</summary>
/// <param name="SidebarCollapsed">Whether the profile sidebar is showing the narrow icon rail.</param>
/// <param name="SidebarWidth">The expanded sidebar width in device-independent pixels.</param>
public sealed record UiState(bool SidebarCollapsed, double SidebarWidth)
{
    /// <summary>Defaults for a first run (or an unreadable state file): expanded, matching the
    /// original hard-coded 280px panel width.</summary>
    public static UiState Default { get; } = new(false, 280);
}

[JsonSourceGenerationOptions(WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UiState))]
public sealed partial class UiJsonContext : JsonSerializerContext;
