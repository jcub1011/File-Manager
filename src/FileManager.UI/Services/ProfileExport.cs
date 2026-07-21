using FileManager.Contracts;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileManager.UI.Services;

/// <summary>Shared profile-export mechanics: writes a profile as a single <c>.json</c> in the on-disk
/// format (via the AOT source-gen context, atomic temp + fsync + rename) so the file round-trips back
/// through Import. Used by both the multi-select export dialog and the single-profile context action.
/// Existing files are never overwritten: names are disambiguated against the destination folder
/// (<see cref="UniqueFileName"/>) and the final rename refuses to replace.</summary>
internal static class ProfileExport
{
    public static void WriteAtomic(string finalPath, Profile profile)
    {
        // Random temp name: never clobbers a pre-existing .tmp. The final move refuses to overwrite,
        // so a file that appeared after the name was picked fails loudly instead of being destroyed.
        string tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, profile, FileManagerJsonContext.Default.Profile);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch (Exception cleanupEx)
            {
                Serilog.Log.Warning(cleanupEx, "Could not clean up export temp file {TempPath}", tempPath);
            }
            throw;
        }
    }

    /// <summary>A filesystem-safe <c>{name}.json</c>, disambiguated with a numeric suffix against both
    /// the names already used in this batch AND the files already in <paramref name="folder"/> — an
    /// existing export must never be silently replaced.</summary>
    public static string UniqueFileName(string profileName, string folder, ISet<string> used)
    {
        string baseName = Sanitize(profileName);
        string candidate = baseName + ".json";
        int n = 2;
        while (!used.Add(candidate) || File.Exists(Path.Combine(folder, candidate)))
            candidate = $"{baseName} ({n++}).json";
        return candidate;
    }

    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }
}
