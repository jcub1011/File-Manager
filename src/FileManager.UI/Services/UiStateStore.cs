using System;
using System.IO;
using System.Text.Json;

namespace FileManager.UI.Services;

/// <summary>Reads and writes the client-side <see cref="UiState"/> (sidebar layout) to a small JSON
/// file under %LOCALAPPDATA%\FileManager. Mirrors the defensive read pattern of
/// <see cref="StartupTheme"/>: any missing/corrupt file or IO failure falls back to
/// <see cref="UiState.Default"/>, and a failed write is logged but never throws — losing a layout
/// preference must never disrupt the app.</summary>
internal static class UiStateStore
{
    public static UiState Read() => Read(UiPaths.UiStateFilePath);

    internal static UiState Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return UiState.Default;

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, UiJsonContext.Default.UiState) ?? UiState.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not read UI state from {File}; using defaults", path);
            return UiState.Default;
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception must not block startup — fall back to defaults.
            Serilog.Log.Error(ex, "Reading UI state from {File} failed unexpectedly; using defaults", path);
            return UiState.Default;
        }
    }

    public static void Write(UiState state) => Write(UiPaths.UiStateFilePath, state);

    internal static void Write(string path, UiState state)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, state, UiJsonContext.Default.UiState);
        }
        catch (Exception ex)
        {
            // Persisting a layout preference is best-effort; a failure here must never disrupt the app.
            Serilog.Log.Warning(ex, "Could not write UI state to {File}", path);
        }
    }
}
