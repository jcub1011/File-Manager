using System;
using System.Collections.Generic;
using FileManager.Contracts;
using FileManager.Contracts.Profiles;

namespace FileManager.Contracts.DryRun;

/// <summary>
/// A dry-run simulation modeled as a bipartite plan graph. The <see cref="SourceFiles"/> and
/// <see cref="DestinationFiles"/> lists are the <em>nodes</em> — the actual living files discovered
/// on disk under the profile's source roots and target roots respectively, pure ground truth with
/// no verdicts. The <see cref="SourceOperations"/> and <see cref="DestinationOperations"/> lists are
/// the <em>edges/annotations</em> — what the program would do, referencing the files they act on
/// <b>by integer index</b> into the two file lists.
/// <para>
/// List order is a stable contract: an operation's <see cref="VirtualFileOperation.SourceIndex"/> /
/// <see cref="VirtualFileOperation.SubjectIndex"/> is a position into these lists, so the engine's
/// emit order must equal the streaming client's assembly order. Truncation happens only at whole
/// per-file-bundle boundaries, so a retained operation never references a dropped file.
/// </para>
/// </summary>
public sealed record DryRunReport
{
    public required Guid ProfileId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    /// <summary>Files discovered under the profile's source roots. Index space for
    /// <see cref="VirtualFileOperation.SourceIndex"/>.</summary>
    public required IReadOnlyList<PhysicalFile> SourceFiles { get; init; }
    /// <summary>Pre-existing files discovered under the profile's target roots. Index space for
    /// <see cref="VirtualFileOperation.SubjectIndex"/>.</summary>
    public required IReadOnlyList<PhysicalFile> DestinationFiles { get; init; }
    /// <summary>One operation per source file describing its fate (Processed / Skipped) and the
    /// disposition of the original. Source-side <see cref="OperationKind"/>s only.</summary>
    public required IReadOnlyList<VirtualFileOperation> SourceOperations { get; init; }
    /// <summary>The destination "after" view: every resulting path with its operation
    /// (New/Overwrite/Rename/Skip/Untouched/Deleted/Unknown). Destination-side
    /// <see cref="OperationKind"/>s only.</summary>
    public required IReadOnlyList<VirtualFileOperation> DestinationOperations { get; init; }
    /// <summary>True when the engine stopped before the scan ran out — the report shows a prefix,
    /// not everything found. Mirror <see cref="OperationKind.Deleted"/> entries are never inferred
    /// on a truncated report.</summary>
    public bool Truncated { get; init; }

    /// <summary>The byte-level space projection (data moved, at-rest growth, per-volume used/free and
    /// bounded-maximum). Null when not computed — the single-frame <c>DryRunHandler</c> path never
    /// fills it, and the streaming path skips it on a truncated report (a partial graph would yield
    /// unsound totals).</summary>
    public SpaceProjection? Space { get; init; }
}

/// <summary>An actual file discovered on disk — pure ground truth, no verdict. <see cref="Length"/>
/// and <see cref="LastWritten"/> come free from the enumeration/stat snapshot (no extra I/O).</summary>
public sealed record PhysicalFile
{
    public required string Path { get; init; }          // absolute
    /// <summary>The source root or target root this file was discovered under — the group/facet key.</summary>
    public required string Root { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset LastWritten { get; init; }
    public bool IsReparsePoint { get; init; }
}

/// <summary>What the program would do to a file. References the physical file(s) it acts on by
/// integer index into <see cref="DryRunReport.SourceFiles"/> / <see cref="DryRunReport.DestinationFiles"/>.
/// List membership (Source vs Destination operations) determines the "side"; there is no side
/// discriminator.</summary>
public sealed record VirtualFileOperation
{
    /// <summary>Source op: the source file's path. Destination op: the resulting path (which may be
    /// a not-yet-existing New/Rename path).</summary>
    public required string Path { get; init; }
    /// <summary>The source/target root this op's path sits under — facet key and cross-tab
    /// relative-path alignment.</summary>
    public required string Root { get; init; }
    public required OperationKind Kind { get; init; }
    /// <summary>Index into <see cref="DryRunReport.SourceFiles"/> for the content origin (a source
    /// op: the file itself; a destination New/Overwrite/Rename op: the incoming content).
    /// <c>-1</c> when there is none.</summary>
    public int SourceIndex { get; init; } = -1;
    /// <summary>Index into <see cref="DryRunReport.DestinationFiles"/> for the pre-existing file this
    /// destination op touches (Overwrite/SkipConflict/SkipUnchanged/Untouched/Deleted/Unknown).
    /// <c>-1</c> when there is none (New/Rename, and all source ops).</summary>
    public int SubjectIndex { get; init; } = -1;
    /// <summary>Source ops only: what happens to the original after a successful copy
    /// (KeepSource / MoveToTrash / MoveToArchive / PermanentDelete). Null when the file does not
    /// process (all-unchanged or skipped).</summary>
    public OnSuccessAction? SourceDisposition { get; init; }
    /// <summary>Display string: e.g. the existing file's mtime, the suffixed rename name, the
    /// deciding filter rule, or the unchanged reason.</summary>
    public string? Detail { get; init; }
}

/// <summary>The fate of a file. Source-side kinds describe a scanned source file; destination-side
/// kinds describe a resulting destination path. The two operation lists keep them apart.</summary>
public enum OperationKind
{
    // ---- Source side ----
    [Tooltip("Processed")]
    Processed,
    [Tooltip("Skipped (Filtered)")]
    SkippedByFilter,
    [Tooltip("Skipped (Unchanged)")]
    SkippedUnchanged,

    // ---- Destination side ----
    [Tooltip("New")]
    New,
    [Tooltip("Overwrite")]
    Overwrite,
    [Tooltip("Renamed")]
    Rename,
    [Tooltip("Skip (Conflict)")]
    SkipConflict,
    [Tooltip("Skip (Unchanged)")]
    SkipUnchanged,
    [Tooltip("Untouched")]
    Untouched,
    [Tooltip("Deleted")]
    Deleted,
    [Tooltip("Unknown")]
    Unknown,
}
