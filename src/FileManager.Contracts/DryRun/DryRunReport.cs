using System;
using System.Collections.Generic;
using FileManager.Contracts;

namespace FileManager.Contracts.DryRun;

public sealed record DryRunReport(
    Guid ProfileId, DateTimeOffset GeneratedAt, IReadOnlyList<DryRunFileResult> Files,
    // Additive (old peers omit it and deserialize the default): true when the engine stopped
    // reporting before the scan ran out — the report shows a prefix, not everything found.
    bool Truncated = false,
    // Additive (old peers omit it and deserialize to []): the destination-only entries the
    // Destinations view needs that are NOT derivable from Files[].Targets — pre-existing files
    // no source writes to (Untouched), and Mirror orphans (Deleted). The write side
    // (New/Overwritten/Renamed) is derived UI-side from Files[].Targets, so it is not repeated here.
    IReadOnlyList<DryRunDestinationEntry>? Destinations = null);

public enum DryRunFileDisposition
{
    [Tooltip("Would Process")]
    WouldProcess,
    [Tooltip("Would Skip (Filtered)")]
    WouldSkipFilter,
    [Tooltip("Would Skip (Unchanged)")]
    WouldSkipUnchanged,
}

public sealed record DryRunFileResult
{
    public required string SourcePath { get; init; }
    // Additive (old peers omit it and deserialize to null): the Source root this file was
    // enumerated under, so the GUI can group/filter a multi-source report by source.
    public string? SourceRoot { get; init; }
    public required DryRunFileDisposition Disposition { get; init; }
    public string? DecidingFilter { get; init; }                  // set for WouldSkipFilter
    /// <summary>Fully token-expanded argv per transformer step, joined for display. Empty if no transformers.</summary>
    public IReadOnlyList<string> ExpandedCommands { get; init; } = [];
    public IReadOnlyList<DryRunTargetAction> Targets { get; init; } = [];
    /// <summary>e.g. "MoveToTrash", "KeepSource". Deletions/overwrites are the report's whole point (spec §8).</summary>
    public string? SourceDisposition { get; init; }
}

public enum DryRunTargetKind
{
    [Tooltip("Write")]
    WouldWrite,
    [Tooltip("Overwrite")]
    WouldOverwrite,
    [Tooltip("Rename")]
    WouldRenameTo,
    [Tooltip("Skip (Conflict)")]
    WouldSkipConflict,
    [Tooltip("Skip (Unchanged)")]
    WouldSkipUnchanged,
    [Tooltip("Unknown")]
    Unknown,
}

public sealed record DryRunTargetAction
{
    public required string TargetPath { get; init; }
    // Additive (old peers omit it and deserialize to null): the profile Target root this action's
    // path sits under, so the Destinations view can group/filter by destination and the Sources view
    // can filter its rows by which destination(s) they land in.
    public string? TargetRoot { get; init; }
    public required DryRunTargetKind Kind { get; init; }
    public string? Detail { get; init; }   // e.g. existing file's mtime, or the suffixed name
}

/// <summary>Disposition of a destination-side entry that the Destinations view can't derive from
/// the source-oriented <see cref="DryRunFileResult.Targets"/>. New/Overwritten/Renamed are derived
/// UI-side from the target actions; only these remain.</summary>
public enum DryRunDestinationDisposition
{
    /// <summary>A file already present under a target root that no source writes to. In
    /// AdditiveArchive it simply stays; it is shown so the resulting tree is complete.</summary>
    [Tooltip("Untouched")]
    Untouched,
    /// <summary>A Mirror orphan: a file under a target root with no corresponding source, which a
    /// real Mirror run would delete. Only emitted for a complete (non-truncated) scan under
    /// <c>SyncMode.Mirror</c>.</summary>
    [Tooltip("Deleted")]
    Deleted,
    /// <summary>A reparse point / unclassifiable entry the sweep declines to judge (never Deleted).</summary>
    [Tooltip("Unknown")]
    Unknown,
}

/// <summary>A destination-only entry (see <see cref="DryRunDestinationDisposition"/>): a file that
/// exists under a profile target root and is not accounted for by any source write.</summary>
public sealed record DryRunDestinationEntry
{
    public required string TargetPath { get; init; }
    /// <summary>The profile Target root this file sits under — the Destinations-view "filter by
    /// destination" key.</summary>
    public required string TargetRoot { get; init; }
    public required DryRunDestinationDisposition Disposition { get; init; }
    public string? Detail { get; init; }
}
