using System;
using System.Collections.Generic;

namespace FileManager.Contracts.DryRun;

public sealed record DryRunReport(
    Guid ProfileId, DateTimeOffset GeneratedAt, IReadOnlyList<DryRunFileResult> Files);

public enum DryRunFileDisposition { WouldProcess, WouldSkipFilter, WouldSkipUnchanged }

public sealed record DryRunFileResult
{
    public required string SourcePath { get; init; }
    public required DryRunFileDisposition Disposition { get; init; }
    public string? DecidingFilter { get; init; }                  // set for WouldSkipFilter
    /// <summary>Fully token-expanded argv per transformer step, joined for display. Empty if no transformers.</summary>
    public IReadOnlyList<string> ExpandedCommands { get; init; } = [];
    public IReadOnlyList<DryRunTargetAction> Targets { get; init; } = [];
    /// <summary>e.g. "MoveToTrash", "KeepSource". Deletions/overwrites are the report's whole point (spec §8).</summary>
    public string? SourceDisposition { get; init; }
}

public enum DryRunTargetKind { WouldWrite, WouldOverwrite, WouldRenameTo, WouldSkipConflict, WouldSkipUnchanged, Unknown }

public sealed record DryRunTargetAction
{
    public required string TargetPath { get; init; }
    public required DryRunTargetKind Kind { get; init; }
    public string? Detail { get; init; }   // e.g. existing file's mtime, or the suffixed name
}
