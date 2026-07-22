namespace FileManager.Contracts.DryRun;

/// <summary>Which stage of a streamed dry run a progress update describes. The stages are strictly
/// ordered: the source scan runs to completion before the destination sweep starts, and
/// <see cref="BuildingLists"/> is emitted once, after discovery, to cover report assembly and
/// transfer.</summary>
public enum DryRunProgressPhase
{
    ScanningSources,
    SweepingDestinations,
    BuildingLists,
}

/// <summary>A point-in-time progress snapshot of a streamed dry run, reported to the caller's
/// <see cref="System.IProgress{T}"/> as progress frames arrive. Counts are cumulative files
/// discovered so far — <see cref="SourceFiles"/> from the source scan, <see cref="DestinationFiles"/>
/// from the file phase plus the destination sweep. Informational only; the assembled report is the
/// source of truth for final totals.</summary>
public sealed record DryRunProgress(DryRunProgressPhase Phase, long SourceFiles, long DestinationFiles);
