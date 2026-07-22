using System;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>The unconditional infrastructure exclusions (I-INFRA-EXCLUDED): the pipeline temp and
/// staging directories, and the transient temp-file marker, that every filesystem walk in the
/// engine must skip. Shared by the source scanner (<see cref="Watching.SourceScanner"/>) and the
/// dry-run destination sweep (<see cref="DryRun.DestinationProjector"/>) so the two agree on what
/// is "the tool's own plumbing" rather than user content.</summary>
public static class InfrastructurePaths
{
    public const string StagingDirectoryName = ".fm_staging";
    private static readonly string[] Directories = [".pipeline_tmp", StagingDirectoryName];
    private const string TempFileMarker = ".fmtmp-";

    /// <summary>The single definition of a target's staged-prior path, shared by the placer and
    /// crash recovery so the two can never disagree. Keyed by job AND target index: one job may have
    /// two targets under the same root whose layouts resolve to the same file NAME in different
    /// directories — without the index they would collide and the second stage would destroy the
    /// first target's prior version.</summary>
    public static string StagedPathFor(string targetRoot, Guid jobId, int targetIndex, string finalFileName)
        => Path.Combine(targetRoot, StagingDirectoryName, jobId.ToString("N"), $"{targetIndex}-{finalFileName}");

    /// <summary>True when a directory name is one of the tool's own infrastructure directories,
    /// which a walk must never descend into or report.</summary>
    public static bool IsInfrastructureDirectoryName(string name)
    {
        // Manual scan over the fixed two-element set — avoids the per-entry enumerator that
        // Enumerable.Contains(comparer) allocates on the scanner's hot path.
        foreach (string infra in Directories)
        {
            if (string.Equals(name, infra, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when a file name carries the transient temp-file marker (a half-written
    /// pipeline artifact), which a walk must skip. Anchored to the placer's actual shape —
    /// <c>&lt;finalName&gt;.fmtmp-&lt;hex job suffix&gt;</c> at the END of the name — so a user file that merely
    /// contains the marker mid-name (e.g. <c>notes.fmtmp-backup.txt</c>) is not silently invisible
    /// to every scan.</summary>
    public static bool IsTempFileName(string fileName)
    {
        int marker = fileName.LastIndexOf(TempFileMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return false;
        int suffixStart = marker + TempFileMarker.Length;
        if (suffixStart >= fileName.Length)
            return false;
        for (int i = suffixStart; i < fileName.Length; i++)
        {
            if (!Uri.IsHexDigit(fileName[i]))
                return false;
        }
        return true;
    }

    /// <summary>True when any segment of <paramref name="path"/> is an infrastructure directory or
    /// its file name is a temp-file marker.</summary>
    public static bool IsInfrastructurePath(string path)
    {
        if (IsTempFileName(Path.GetFileName(path)))
            return true;
        foreach (string segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (IsInfrastructureDirectoryName(segment))
                return true;
        }
        return false;
    }
}
