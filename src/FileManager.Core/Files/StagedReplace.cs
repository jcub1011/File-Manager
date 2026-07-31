using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>The staged-overwrite primitive (§4.3, I-PRIOR): swap <paramref name="tempPath"/> into
/// <paramref name="finalPath"/> while the version it replaces lands at <paramref name="stagedPath"/>
/// for rollback. This is the single most safety-critical write in the product — it is where a user's
/// existing file is exchanged for a new one — and it has exactly two callers, the live placer and
/// crash recovery, which previously carried their own copies of it. The knowledge here is one rule
/// with one reason to change (a new filesystem/SMB behaviour), so it lives in one place.
/// </summary>
internal static class StagedReplace
{
    /// <summary>One Win32 ReplaceFile when the volume supports it — atomic, so there is no window
    /// where the final name is absent (I-PRIOR). Volumes that reject it (some SMB servers) fall back
    /// to a two-step move, which is deliberately IDEMPOTENT: if a previous attempt already moved the
    /// prior version out (final now absent), it is not moved again, it just finishes by moving the temp
    /// into place. Without that guard, a retry — from the placer's transient-retry policy or from
    /// recovery replaying the same journal rows on a later startup — after the first move succeeded and
    /// the second failed would throw FileNotFound forever and strand the final name absent.</summary>
    public static void Execute(string tempPath, string finalPath, string stagedPath, ILogger logger)
    {
        try
        {
            File.Replace(tempPath, finalPath, stagedPath, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "File.Replace rejected for \"{Final}\"; falling back to two-step move", finalPath);
            if (File.Exists(finalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                File.Move(finalPath, stagedPath, overwrite: false);
            }
            File.Move(tempPath, finalPath, overwrite: false);
        }
    }
}
