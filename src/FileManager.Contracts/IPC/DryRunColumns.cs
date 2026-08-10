using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.IPC;

/// <summary>One streamed chunk's discovered files, as a column per field rather than an object per file.
/// Mirrors <see cref="DryRunFile"/>'s properties exactly — same names, same types, same order — so the
/// two shapes stay comparable by eye and a field added to one is obvious in the other.
///
/// <para>Every column holds the same number of entries; index <c>i</c> across all six is one file. That
/// is checked at the trust boundary (<see cref="DryRunColumns.FindRaggedColumn"/>), never assumed.</para></summary>
public sealed class DryRunFileColumns
{
    /// <summary>Index of the containing directory in the chunk stream's assembled directory table.</summary>
    public List<int> DirIndex { get; set; } = [];
    public List<string> FileName { get; set; } = [];
    /// <summary>Index of the source/target root this file was discovered under — the group/facet key.</summary>
    public List<int> RootDirIndex { get; set; } = [];
    public List<long> Length { get; set; } = [];
    public List<System.DateTimeOffset> LastWritten { get; set; } = [];
    public List<bool> IsReparsePoint { get; set; } = [];

    /// <summary>Files in this group. <see cref="DirIndex"/> is the reference column — the others are
    /// checked against it at the trust boundary, so reading this before that check is not safe.</summary>
    [JsonIgnore]
    public int Count => DirIndex.Count;

    /// <summary>Drops every column's contents while keeping their capacity, so one set of buffers serves
    /// a whole stream. Used by the service's converter between chunks.</summary>
    public void Clear()
    {
        DirIndex.Clear();
        FileName.Clear();
        RootDirIndex.Clear();
        Length.Clear();
        LastWritten.Clear();
        IsReparsePoint.Clear();
    }

    public void Add(int dirIndex, string fileName, int rootDirIndex, long length, System.DateTimeOffset lastWritten, bool isReparsePoint)
    {
        DirIndex.Add(dirIndex);
        FileName.Add(fileName);
        RootDirIndex.Add(rootDirIndex);
        Length.Add(length);
        LastWritten.Add(lastWritten);
        IsReparsePoint.Add(isReparsePoint);
    }
}

/// <summary>One streamed chunk's operations, as a column per field. Mirrors <see cref="DryRunOperation"/>
/// exactly; index semantics (<see cref="SourceIndex"/>/<see cref="SubjectIndex"/>) are unchanged —
/// positions into the stream's assembled file lists.
///
/// <para><c>SourceDisposition</c> and <c>Detail</c> are sparse: most operations have neither. A column
/// cannot omit an element the way <c>DefaultIgnoreCondition.WhenWritingNull</c> omits a property, so those
/// two write an explicit <c>null</c> per operation that lacks them. That cost is real and was measured
/// with it included — the columnar frame is still 52% smaller.</para></summary>
public sealed class DryRunOperationColumns
{
    /// <summary>Index of the resulting path's directory in the assembled directory table.</summary>
    public List<int> DirIndex { get; set; } = [];
    public List<string> FileName { get; set; } = [];
    /// <summary>Index of the source/target root this op's path sits under — facet key and cross-tab
    /// relative-path alignment.</summary>
    public List<int> RootDirIndex { get; set; } = [];
    public List<OperationKind> Kind { get; set; } = [];
    /// <summary>Position in the assembled source-file list for the content origin; <c>-1</c> when none.</summary>
    public List<int> SourceIndex { get; set; } = [];
    /// <summary>Position in the assembled destination-file list for the pre-existing file this op
    /// touches; <c>-1</c> when none (New/Rename, and all source ops).</summary>
    public List<int> SubjectIndex { get; set; } = [];
    /// <summary>Source ops only: what happens to the original after a successful copy. Null when the
    /// file does not process.</summary>
    public List<OnSuccessAction?> SourceDisposition { get; set; } = [];
    /// <summary>Display string: the existing file's mtime, the suffixed rename name, the deciding filter
    /// rule, or the unchanged reason.</summary>
    public List<string?> Detail { get; set; } = [];
    /// <summary>Source ops only: an <see cref="OperationKindMask"/> over the kinds this source's
    /// destination operations take; <c>0</c> elsewhere. See <see cref="DryRunOperation.TargetKinds"/> for
    /// why a paged Sources tab needs it and why it is a set rather than a chosen glyph.</summary>
    public List<int> TargetKinds { get; set; } = [];

    /// <summary>Operations in this group. See <see cref="DryRunFileColumns.Count"/> on ordering versus
    /// the raggedness check.</summary>
    [JsonIgnore]
    public int Count => DirIndex.Count;

    public void Clear()
    {
        DirIndex.Clear();
        FileName.Clear();
        RootDirIndex.Clear();
        Kind.Clear();
        SourceIndex.Clear();
        SubjectIndex.Clear();
        SourceDisposition.Clear();
        Detail.Clear();
        TargetKinds.Clear();
    }

    public void Add(
        int dirIndex, string fileName, int rootDirIndex, OperationKind kind,
        int sourceIndex, int subjectIndex, OnSuccessAction? sourceDisposition, string? detail,
        int targetKinds = 0)
    {
        DirIndex.Add(dirIndex);
        FileName.Add(fileName);
        RootDirIndex.Add(rootDirIndex);
        Kind.Add(kind);
        SourceIndex.Add(sourceIndex);
        SubjectIndex.Add(subjectIndex);
        SourceDisposition.Add(sourceDisposition);
        Detail.Add(detail);
        TargetKinds.Add(targetKinds);
    }
}

/// <summary>Trust-boundary checks that only a columnar wire shape needs.
///
/// <para>A record-wise encoding could not express a half-built row: an object either had a
/// <c>FileName</c> or the JSON was malformed. Columns can — a producer bug or a corrupt frame can deliver
/// 10 <c>DirIndex</c> values and 5 <c>FileName</c>s, and a consumer zipping them would pair one file's
/// name with another's directory. In a dry-run preview that a user approves destructive file operations
/// from, showing the wrong path against the wrong operation is the failure this exists to make
/// impossible. Reject, do not repair: a chunk that disagrees with itself has no defensible
/// interpretation.</para></summary>
public static class DryRunColumns
{
    /// <summary>Builds a chunk from the per-record types — the bridge for callers that still hold
    /// records: <c>DryRunRowStore.FromReport</c>, and the test fixtures that describe a chunk far more
    /// readably as a list of objects than as fifteen parallel lists.
    ///
    /// <para>Deliberately <em>not</em> on any hot path. The streamed producer builds columns directly
    /// (that is the whole point); this exists so there is one records-to-columns conversion in the
    /// codebase rather than one per fixture, each free to get a column subtly wrong.</para></summary>
    public static DryRunChunkResponse ToChunk(
        IReadOnlyList<DryRunDirectory>? directories = null,
        IReadOnlyList<DryRunFile>? sourceFiles = null,
        IReadOnlyList<DryRunFile>? destinationFiles = null,
        IReadOnlyList<DryRunOperation>? sourceOperations = null,
        IReadOnlyList<DryRunOperation>? destinationOperations = null)
    {
        DryRunChunkResponse chunk = new();
        if (directories is not null)
        {
            foreach (DryRunDirectory dir in directories)
            {
                chunk.DirectoryName.Add(dir.Name);
                chunk.DirectoryParentIndex.Add(dir.ParentIndex);
            }
        }
        AddFiles(chunk.SourceFiles, sourceFiles);
        AddFiles(chunk.DestinationFiles, destinationFiles);
        AddOperations(chunk.SourceOperations, sourceOperations);
        AddOperations(chunk.DestinationOperations, destinationOperations);
        return chunk;
    }

    /// <summary>Rehydrates a file column group into the per-record type, one <see cref="DryRunFile"/> at a
    /// time. The inverse of <see cref="ToChunk"/>, and under the same warning, more sharply: this
    /// allocates exactly the per-record object the columnar wire exists to avoid. It is for the two
    /// callers that have already accepted that cost — the assembling <c>DryRunStreamAsync</c> overload,
    /// whose contract is to hand back a whole <c>DryRunReport</c>, and test assertions. <strong>Never call
    /// it on an ingest path.</strong></summary>
    public static IEnumerable<DryRunFile> ToRecords(DryRunFileColumns columns)
    {
        System.ArgumentNullException.ThrowIfNull(columns);
        for (int i = 0; i < columns.Count; i++)
        {
            yield return new DryRunFile
            {
                DirIndex = columns.DirIndex[i],
                FileName = columns.FileName[i],
                RootDirIndex = columns.RootDirIndex[i],
                Length = columns.Length[i],
                LastWritten = columns.LastWritten[i],
                IsReparsePoint = columns.IsReparsePoint[i],
            };
        }
    }

    /// <summary>Rehydrates an operation column group. See <see cref="ToRecords(DryRunFileColumns)"/> for
    /// when this is and is not appropriate.</summary>
    public static IEnumerable<DryRunOperation> ToRecords(DryRunOperationColumns columns)
    {
        System.ArgumentNullException.ThrowIfNull(columns);
        for (int i = 0; i < columns.Count; i++)
        {
            yield return new DryRunOperation
            {
                DirIndex = columns.DirIndex[i],
                FileName = columns.FileName[i],
                RootDirIndex = columns.RootDirIndex[i],
                Kind = columns.Kind[i],
                SourceIndex = columns.SourceIndex[i],
                SubjectIndex = columns.SubjectIndex[i],
                SourceDisposition = columns.SourceDisposition[i],
                Detail = columns.Detail[i],
                TargetKinds = columns.TargetKinds[i],
            };
        }
    }

    /// <summary>Rehydrates the chunk's two directory columns into <see cref="DryRunDirectory"/> entries,
    /// for callers that need to hand them to <c>DryRunDirectoryTable.Materialize</c>. Streaming consumers
    /// should append via <c>DryRunDirectoryPathBuilder.Append(string, int)</c> instead and never build
    /// these.</summary>
    public static IEnumerable<DryRunDirectory> ToDirectoryRecords(DryRunChunkResponse chunk)
    {
        System.ArgumentNullException.ThrowIfNull(chunk);
        for (int i = 0; i < chunk.DirectoryName.Count; i++)
            yield return new DryRunDirectory(chunk.DirectoryName[i], chunk.DirectoryParentIndex[i]);
    }

    private static void AddFiles(DryRunFileColumns columns, IReadOnlyList<DryRunFile>? files)
    {
        if (files is null)
            return;
        foreach (DryRunFile f in files)
            columns.Add(f.DirIndex, f.FileName, f.RootDirIndex, f.Length, f.LastWritten, f.IsReparsePoint);
    }

    private static void AddOperations(DryRunOperationColumns columns, IReadOnlyList<DryRunOperation>? operations)
    {
        if (operations is null)
            return;
        foreach (DryRunOperation o in operations)
            columns.Add(o.DirIndex, o.FileName, o.RootDirIndex, o.Kind, o.SourceIndex, o.SubjectIndex, o.SourceDisposition, o.Detail, o.TargetKinds);
    }

    /// <summary>The first column in the chunk whose length disagrees with its group's reference column,
    /// or <c>null</c> when every group is rectangular.</summary>
    public static string? FindRaggedColumn(DryRunChunkResponse chunk)
    {
        System.ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.DirectoryParentIndex.Count != chunk.DirectoryName.Count)
            return $"DirectoryParentIndex has {chunk.DirectoryParentIndex.Count} entries against DirectoryName's {chunk.DirectoryName.Count}";
        return FindRagged(chunk.SourceFiles, nameof(chunk.SourceFiles))
            ?? FindRagged(chunk.DestinationFiles, nameof(chunk.DestinationFiles))
            ?? FindRagged(chunk.SourceOperations, nameof(chunk.SourceOperations))
            ?? FindRagged(chunk.DestinationOperations, nameof(chunk.DestinationOperations));
    }

    private static string? FindRagged(DryRunFileColumns files, string group)
    {
        int n = files.DirIndex.Count;
        return Check(group, nameof(files.FileName), files.FileName.Count, n)
            ?? Check(group, nameof(files.RootDirIndex), files.RootDirIndex.Count, n)
            ?? Check(group, nameof(files.Length), files.Length.Count, n)
            ?? Check(group, nameof(files.LastWritten), files.LastWritten.Count, n)
            ?? Check(group, nameof(files.IsReparsePoint), files.IsReparsePoint.Count, n);
    }

    private static string? FindRagged(DryRunOperationColumns ops, string group)
    {
        int n = ops.DirIndex.Count;
        return Check(group, nameof(ops.FileName), ops.FileName.Count, n)
            ?? Check(group, nameof(ops.RootDirIndex), ops.RootDirIndex.Count, n)
            ?? Check(group, nameof(ops.Kind), ops.Kind.Count, n)
            ?? Check(group, nameof(ops.SourceIndex), ops.SourceIndex.Count, n)
            ?? Check(group, nameof(ops.SubjectIndex), ops.SubjectIndex.Count, n)
            ?? Check(group, nameof(ops.SourceDisposition), ops.SourceDisposition.Count, n)
            ?? Check(group, nameof(ops.Detail), ops.Detail.Count, n)
            ?? Check(group, nameof(ops.TargetKinds), ops.TargetKinds.Count, n);
    }

    private static string? Check(string group, string column, int actual, int expected) =>
        actual == expected ? null : $"{group}.{column} has {actual} entries against DirIndex's {expected}";

    /// <summary>Returns a description of the first file/op whose <c>DirIndex</c> or <c>RootDirIndex</c>
    /// falls outside a directory table of <paramref name="directoryCount"/> entries, or <c>null</c> when
    /// every reference is in range. The columnar counterpart of
    /// <c>DryRunDirectoryTable.FindInvalidReference</c>, with identical semantics: consumers index the
    /// materialized path array by these values, so an out-of-range reference from a corrupt or buggy
    /// producer is rejected here rather than crashing deep in a consumer. The <c>(uint)</c> casts fold the
    /// negative and too-large checks into one comparison.
    ///
    /// <para>Call only after <see cref="FindRaggedColumn"/> has passed — this reads
    /// <c>FileName</c> positionally to name the offender.</para></summary>
    public static string? FindInvalidReference(int directoryCount, DryRunFileColumns files, DryRunOperationColumns operations)
    {
        System.ArgumentNullException.ThrowIfNull(files);
        System.ArgumentNullException.ThrowIfNull(operations);
        for (int i = 0; i < files.Count; i++)
        {
            if ((uint)files.DirIndex[i] >= (uint)directoryCount)
                return $"file '{files.FileName[i]}' DirIndex {files.DirIndex[i]}";
            if ((uint)files.RootDirIndex[i] >= (uint)directoryCount)
                return $"file '{files.FileName[i]}' RootDirIndex {files.RootDirIndex[i]}";
        }
        for (int i = 0; i < operations.Count; i++)
        {
            if ((uint)operations.DirIndex[i] >= (uint)directoryCount)
                return $"operation '{operations.FileName[i]}' DirIndex {operations.DirIndex[i]}";
            if ((uint)operations.RootDirIndex[i] >= (uint)directoryCount)
                return $"operation '{operations.FileName[i]}' RootDirIndex {operations.RootDirIndex[i]}";
        }
        return null;
    }
}
