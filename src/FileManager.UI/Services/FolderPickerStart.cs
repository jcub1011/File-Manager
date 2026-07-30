using Serilog;
using System;
using System.IO;

namespace FileManager.UI.Services;

/// <summary>Resolves where a folder picker should open given the folder the caller already points
/// at. Split out of <see cref="StorageProviderFolderPicker"/> so the rule is testable without a
/// window (the Avalonia half is only the string → IStorageFolder lookup).</summary>
internal static class FolderPickerStart
{
    /// <summary>Where the picker should open for a previously chosen folder: its PARENT, so the
    /// chosen folder itself is the visible entry. Null when nothing usable was chosen (blank,
    /// malformed, or gone) — the caller falls back to Downloads. A drive root has no parent, so it
    /// opens at itself.</summary>
    public static string? ParentOf(string? chosen)
    {
        if (string.IsNullOrWhiteSpace(chosen))
            return null;
        try
        {
            if (!Directory.Exists(chosen))
                return null;
            // TrimEndingDirectorySeparator first: GetDirectoryName("C:\a\b\") answers "C:\a\b",
            // which would open INSIDE the chosen folder instead of beside it.
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
            return Path.GetDirectoryName(full) is { Length: > 0 } parent ? parent : full;
        }
        catch (Exception ex)
        {
            // Corrupt settings or a hand-typed path can make even GetFullPath throw; a picker that
            // opens at Downloads is a far better outcome than a faulted Browse command.
            Log.Debug(ex, "Could not resolve a picker start folder from {Chosen}", chosen);
            return null;
        }
    }
}
