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
/// <b>by integer index</b> into the two file lists. Paths are normalized: files/ops carry
/// (<c>DirIndex</c>, <c>FileName</c>) into the shared <see cref="Directories"/> table rather than
/// flat absolute strings, so sibling files share their directory chain structurally.
/// <para>
/// List order is a stable contract: an operation's <see cref="DryRunOperation.SourceIndex"/> /
/// <see cref="DryRunOperation.SubjectIndex"/> is a position into these lists, so the engine's
/// emit order must equal the streaming client's assembly order. Truncation happens only at whole
/// per-file-bundle boundaries, so a retained operation never references a dropped file.
/// </para>
/// </summary>
public sealed record DryRunReport
{
    public required Guid ProfileId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    /// <summary>The shared directory table every file/op's <c>DirIndex</c>/<c>RootDirIndex</c>
    /// resolves into (see <see cref="DryRunDirectory"/>). Parents precede children, so
    /// <see cref="DryRunDirectoryTable.Materialize"/> resolves it in one forward pass.</summary>
    public required IReadOnlyList<DryRunDirectory> Directories { get; init; }
    /// <summary>Files discovered under the profile's source roots. Index space for
    /// <see cref="DryRunOperation.SourceIndex"/>.</summary>
    public required IReadOnlyList<DryRunFile> SourceFiles { get; init; }
    /// <summary>Pre-existing files discovered under the profile's target roots. Index space for
    /// <see cref="DryRunOperation.SubjectIndex"/>.</summary>
    public required IReadOnlyList<DryRunFile> DestinationFiles { get; init; }
    /// <summary>One operation per source file describing its fate (Processed / Skipped) and the
    /// disposition of the original. Source-side <see cref="OperationKind"/>s only.</summary>
    public required IReadOnlyList<DryRunOperation> SourceOperations { get; init; }
    /// <summary>The destination "after" view: every resulting path with its operation
    /// (New/Overwrite/Rename/Skip/Untouched/Deleted/Unknown). Destination-side
    /// <see cref="OperationKind"/>s only.</summary>
    public required IReadOnlyList<DryRunOperation> DestinationOperations { get; init; }
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

/// <summary>One directory in a report's shared table. A root entry (<see cref="ParentIndex"/> == -1)
/// stores the path root in full (<c>C:\</c>, <c>\\server\share</c>); every other entry stores a
/// single segment name. <see cref="ParentIndex"/> always refers to an earlier entry (parents precede
/// children), so a single forward pass can materialize absolute paths. The table exists because
/// sibling files repeat their entire directory chain — sharing the chain structurally is what keeps
/// a 500k-file report's paths from costing hundreds of MB as flat strings.</summary>
public sealed record DryRunDirectory(string Name, int ParentIndex);

/// <summary>The wire form of a discovered file: its directory as an index into the report's shared
/// <see cref="DryRunReport.Directories"/> table plus the file name — never a flat absolute path.
/// Otherwise mirrors <see cref="PhysicalFile"/>, which remains the engine's in-memory currency
/// (evaluation and hashing need absolute paths) but no longer crosses the IPC boundary.
///
/// <para>A mutable class rather than a record, deliberately: the service pools these on the streamed
/// path (a dry run over 500k files would otherwise allocate one per entry, and in-run peak commit
/// tracks that churn), and the batched builder remaps indices in place instead of <c>with{}</c>
/// copies. Mutability is invisible on the wire (<c>set</c> serializes exactly like <c>init</c>) and
/// value equality is preserved by hand below — but note <c>==</c> is now reference equality; compare
/// with <see cref="Equals(DryRunFile?)"/>. Property declaration order is the wire order
/// (source-generated serialization emits in declaration order) — do not reorder.</para></summary>
public sealed class DryRunFile : IEquatable<DryRunFile>
{
    /// <summary>Index of the containing directory in <see cref="DryRunReport.Directories"/>.</summary>
    public required int DirIndex { get; set; }
    public required string FileName { get; set; }
    /// <summary>Index of the source/target root this file was discovered under — the group/facet key.</summary>
    public required int RootDirIndex { get; set; }
    public required long Length { get; set; }
    public required DateTimeOffset LastWritten { get; set; }
    public bool IsReparsePoint { get; set; }

    public bool Equals(DryRunFile? other) =>
        other is not null
        && DirIndex == other.DirIndex
        && FileName == other.FileName
        && RootDirIndex == other.RootDirIndex
        && Length == other.Length
        && LastWritten.Equals(other.LastWritten)
        && IsReparsePoint == other.IsReparsePoint;

    public override bool Equals(object? obj) => Equals(obj as DryRunFile);

    public override int GetHashCode() =>
        HashCode.Combine(DirIndex, FileName, RootDirIndex, Length, LastWritten, IsReparsePoint);
}

/// <summary>The wire form of <see cref="VirtualFileOperation"/>: the op's path as a directory-table
/// index plus file name. Index semantics (<see cref="SourceIndex"/>/<see cref="SubjectIndex"/>) are
/// unchanged — positions into the report's file lists.
/// <para>A mutable class for the same pooling/in-place-remap reasons as <see cref="DryRunFile"/> —
/// see its doc for the equality and property-order caveats.</para></summary>
public sealed class DryRunOperation : IEquatable<DryRunOperation>
{
    /// <summary>Index of the resulting path's directory in <see cref="DryRunReport.Directories"/>.</summary>
    public required int DirIndex { get; set; }
    public required string FileName { get; set; }
    /// <summary>Index of the source/target root this op's path sits under — facet key and cross-tab
    /// relative-path alignment.</summary>
    public required int RootDirIndex { get; set; }
    public required OperationKind Kind { get; set; }
    /// <summary>Index into <see cref="DryRunReport.SourceFiles"/> for the content origin. <c>-1</c>
    /// when there is none.</summary>
    public int SourceIndex { get; set; } = -1;
    /// <summary>Index into <see cref="DryRunReport.DestinationFiles"/> for the pre-existing file this
    /// destination op touches. <c>-1</c> when there is none (New/Rename, and all source ops).</summary>
    public int SubjectIndex { get; set; } = -1;
    /// <summary>Source ops only: what happens to the original after a successful copy. Null when the
    /// file does not process.</summary>
    public OnSuccessAction? SourceDisposition { get; set; }
    /// <summary>Display string: e.g. the existing file's mtime, the suffixed rename name, the
    /// deciding filter rule, or the unchanged reason.</summary>
    public string? Detail { get; set; }

    public bool Equals(DryRunOperation? other) =>
        other is not null
        && DirIndex == other.DirIndex
        && FileName == other.FileName
        && RootDirIndex == other.RootDirIndex
        && Kind == other.Kind
        && SourceIndex == other.SourceIndex
        && SubjectIndex == other.SubjectIndex
        && SourceDisposition == other.SourceDisposition
        && Detail == other.Detail;

    public override bool Equals(object? obj) => Equals(obj as DryRunOperation);

    public override int GetHashCode() =>
        HashCode.Combine(DirIndex, FileName, RootDirIndex, Kind, SourceIndex, SubjectIndex, SourceDisposition, Detail);
}

/// <summary>Read-only view of a discovered file's fields, implemented by both the immutable
/// <see cref="PhysicalFile"/> record and the engine's pooled mutable carrier on the dry-run spool
/// read-back path. Widening the streamed <c>DryRunChunk</c> and the chunk consumers to this view lets
/// a spilled run replay through recycled carriers instead of a second set of records, without any
/// consumer knowing which concrete type it holds. <c>IReadOnlyList&lt;PhysicalFile&gt;</c> is
/// covariantly assignable to <c>IReadOnlyList&lt;IPhysicalFileView&gt;</c>, so existing callers that
/// pass concrete lists (e.g. the destination sweep) keep compiling unchanged.</summary>
public interface IPhysicalFileView
{
    string Path { get; }
    string Root { get; }
    long Length { get; }
    DateTimeOffset LastWritten { get; }
    bool IsReparsePoint { get; }
}

/// <summary>Read-only view of an operation's fields — the <see cref="VirtualFileOperation"/> analogue
/// of <see cref="IPhysicalFileView"/>. The index fields (<see cref="SourceIndex"/>/
/// <see cref="SubjectIndex"/>) are read-only here; the engine's pooled carrier mutates them through
/// its concrete type during the streamed global-index remap, never through this view.</summary>
public interface IFileOperationView
{
    string Path { get; }
    string Root { get; }
    OperationKind Kind { get; }
    int SourceIndex { get; }
    int SubjectIndex { get; }
    OnSuccessAction? SourceDisposition { get; }
    string? Detail { get; }
}

/// <summary>An actual file discovered on disk — pure ground truth, no verdict. <see cref="Length"/>
/// and <see cref="LastWritten"/> come free from the enumeration/stat snapshot (no extra I/O).
/// Engine-internal currency: evaluation, hashing, and the destination sweep work on absolute
/// paths; the wire carries <see cref="DryRunFile"/> instead.</summary>
public sealed record PhysicalFile : IPhysicalFileView
{
    public required string Path { get; init; }          // absolute
    /// <summary>The source root or target root this file was discovered under — the group/facet key.</summary>
    public required string Root { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset LastWritten { get; init; }
    public bool IsReparsePoint { get; init; }
}

/// <summary>What the program would do to a file. References the physical file(s) it acts on by
/// integer index into the report's file lists. List membership (Source vs Destination operations)
/// determines the "side"; there is no side discriminator. Engine-internal currency: the wire
/// carries <see cref="DryRunOperation"/> instead.</summary>
public sealed record VirtualFileOperation : IFileOperationView
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
