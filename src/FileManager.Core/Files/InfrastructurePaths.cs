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
    private static readonly string[] Directories = [".pipeline_tmp", ".fm_staging"];
    private const string TempFileMarker = ".fmtmp-";

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
    /// pipeline artifact), which a walk must skip.</summary>
    public static bool IsTempFileName(string fileName) =>
        fileName.Contains(TempFileMarker, StringComparison.OrdinalIgnoreCase);

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
