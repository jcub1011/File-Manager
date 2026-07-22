using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using System.Collections.Generic;

namespace FileManager.Core.Platform;

public interface IMetadataPreserver
{
    /// <summary>Detects lossy metadata transitions before the copy (spec §6.4).</summary>
    Result<MetadataLossReport, string> Inspect(string sourcePath, string targetDirectory);

    /// <summary>Applies timestamps/ACLs best-effort to the temp file before rename.</summary>
    Result Apply(string fromPath, string toPath, MetadataOnConflict onConflict);
}

public sealed record MetadataLossReport(bool LossDetected, IReadOnlyList<string> Details);
