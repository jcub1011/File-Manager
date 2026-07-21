using FileManager.Contracts;
using FileManager.Contracts.Profiles;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileManager.UI.Services;

/// <summary>Shared profile-export mechanics: writes a profile as a single <c>.json</c> in the on-disk
/// format (via the AOT source-gen context, atomic temp + fsync + rename) so the file round-trips back
/// through Import. Used by both the multi-select export dialog and the single-profile context action.</summary>
internal static class ProfileExport
{
    public static void WriteAtomic(string finalPath, Profile profile)
    {
        string tempPath = finalPath + ".tmp";
        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, profile, FileManagerJsonContext.Default.Profile);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, finalPath, overwrite: true);
    }

    /// <summary>A filesystem-safe <c>{name}.json</c>, disambiguated against names already used in this
    /// batch with a numeric suffix.</summary>
    public static string UniqueFileName(string profileName, ISet<string> used)
    {
        string baseName = Sanitize(profileName);
        string candidate = baseName + ".json";
        int n = 2;
        while (!used.Add(candidate))
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
