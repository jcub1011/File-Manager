using System;
using System.Collections.Generic;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;

namespace FileManager.UI.ViewModels;

/// <summary>The dry-run preview's data model: one column per field instead of one object per row.
///
/// <para>Why: the previous shape allocated a <c>DryRunFileRow</c> + a target list + a
/// <c>DryRunDestinationRow</c> + an entry list per source file, and materialized every destination
/// operation <b>twice</b> (once as a target of its source row, once as an entry of its destination
/// row) — ~2.5M objects at the 500k cap, measured at 219 MB retained. Here a source file costs ~34
/// bytes of column plus its interned name, and an operation ~29, with the row types reduced to
/// <c>(store, index)</c> handles that project a column read into the property the XAML binds. The
/// figures the change is measured against live in
/// <c>tests/FileManager.UI.Tests/DryRunViewModelMemoryTests.cs</c>.</para>
///
/// <para>It is also the stream's <see cref="IDryRunChunkSink"/>, which is the other half of the win:
/// chunks fold straight into the columns and are dropped, so the assembled <c>DryRunReport</c> — the
/// thing that used to be live at the same time as the rows it was being projected into — never
/// exists in the UI process at all.</para>
///
/// <para>Lifecycle: construct → <see cref="OnChunk"/> per frame (single-threaded, receive order) →
/// <see cref="Complete"/> once → read-only forever after. Every accessor below is safe to call
/// concurrently on a completed store; the tabs' parallel filter and sort passes rely on that.</para></summary>
public sealed class DryRunRowStore : IDryRunChunkSink
{
    /// <summary>An empty completed store — what the tabs hold before the first run and after a clear,
    /// so row accessors never have to null-check a store.</summary>
    public static readonly DryRunRowStore Empty = CreateEmpty();

    // ── Shared path table ────────────────────────────────────────────────────────────────────────
    // Resolved as entries arrive (parents precede children, so no buffering); this is the one place
    // directory strings exist. Every row's DirPath / Root is a reference into it.
    private readonly DryRunDirectoryPathBuilder _directories = new();
    private string[] _dirPaths = [];

    // ── Source-file columns, indexed 0..SourceCount ──────────────────────────────────────────────
    private readonly ColumnBuffer<int> _srcDir = new();
    private readonly ColumnBuffer<int> _srcRootDir = new();
    private readonly ColumnBuffer<string> _srcName = new();
    private readonly ColumnBuffer<long> _srcSize = new();
    // 0 means "no source operation seen for this file yet"; otherwise (byte)(kind + 1). The sentinel
    // is what makes "first operation for a duplicate SourceIndex wins" expressible without a second
    // column, and keeps the no-operation default (Processed) distinguishable from a real Processed.
    private readonly ColumnBuffer<byte> _srcKind = new();
    // (byte)(OnSuccessAction + 1); 0 means null — the file does not process.
    private readonly ColumnBuffer<byte> _srcDisposition = new();
    private readonly ColumnBuffer<int> _srcDetail = new();

    // ── Destination-operation columns, indexed by STORAGE index (arrival order) ──────────────────
    private readonly ColumnBuffer<int> _opDir = new();
    private readonly ColumnBuffer<int> _opRootDir = new();
    private readonly ColumnBuffer<string> _opName = new();
    private readonly ColumnBuffer<byte> _opKind = new();
    private readonly ColumnBuffer<int> _opDetail = new();
    private readonly ColumnBuffer<long> _opSize = new();

    // Dropped by Complete once they have done their work — see the field-by-field notes there.
    private ColumnBuffer<int>? _opSource;
    private ColumnBuffer<int>? _opSubject;
    private ColumnBuffer<long>? _destFileLength;
    private Dictionary<string, string>? _names = new(StringComparer.Ordinal);
    private Dictionary<string, int>? _detailIds = new(StringComparer.Ordinal);
    private List<DeferredSourceOp>? _deferredSourceOps;   // see ApplySourceOperation; normally stays null

    // Detail strings are a small closed set in practice ("exclude *.tmp", "identical content
    // (SHA-256)", "kept", a conflict rename's target …) but arrive as a fresh instance per record.
    private readonly List<string> _details = [];

    /// <summary>CSR over the destination operations: positions <c>[TargetStart(i), TargetEnd(i))</c>
    /// are source <c>i</c>'s operations, and <c>[NoSourceStart, OperationCount)</c> are the ones with
    /// no originating source (a Mirror orphan, a kept-around conflict original, a pre-existing
    /// untouched file), each of which becomes its own destination row.</summary>
    private int[] _targetOffset = [0];

    /// <summary>CSR position → storage index, or null when the two coincide. The engine emits each
    /// source file's operations together and in source order, so arrival order usually IS grouped
    /// order and this array is not allocated at all — but that is an observed property of the
    /// producer, not a contract, so the general path stays.</summary>
    private int[]? _order;

    // Construction goes through CreateForIngest / FromReport / Empty so the drop-after-Complete
    // columns are always allocated together with the rest.
    private DryRunRowStore()
    {
        _opSource = new ColumnBuffer<int>();
        _opSubject = new ColumnBuffer<int>();
        _destFileLength = new ColumnBuffer<long>();
    }

    public bool IsCompleted { get; private set; }

    public int SourceCount => _srcDir.Count;

    /// <summary>Destination operations reachable through the CSR. Can be below the number ingested:
    /// an operation whose <c>SourceIndex</c> points past the end of the source list is dropped, which
    /// is exactly what the previous <c>ToLookup</c>-shaped grouping did with such a key.</summary>
    public int OperationCount { get; private set; }

    /// <summary>First CSR position of the operations that have no originating source file.</summary>
    public int NoSourceStart { get; private set; }

    /// <summary>The folder every displayed source path is shown relative to; null when the sources
    /// span drives, so full paths are shown.</summary>
    public string? SourceCommonRoot { get; private set; }

    /// <summary>The destination-side counterpart of <see cref="SourceCommonRoot"/>.</summary>
    public string? DestinationCommonRoot { get; private set; }

    /// <summary>Replaces the common roots this store derived from its own rows.
    ///
    /// <para><b>For a PAGE store, whose rows are a window rather than the whole plan.</b> Both roots are
    /// a property of every row in a view, and a page can only see its own — so a page holding files from
    /// one directory would compute a much deeper root than the plan's, and every path on screen would be
    /// shown relative to something that shifted as the user scrolled. The paged store therefore computes
    /// the roots once, from the plan's facet keys, and stamps them here.</para>
    ///
    /// <para>Only meaningful after <see cref="Complete"/>, which is where the derived values are set;
    /// calling it before would simply be overwritten.</para></summary>
    public void UseCommonRoots(string? source, string? destination)
    {
        SourceCommonRoot = source;
        DestinationCommonRoot = destination;
    }

    // Blast-radius banner numbers, folded during Complete so nothing re-walks the columns for them.
    public int OverwriteCount { get; private set; }
    public int RenameCount { get; private set; }
    public int DisposalCount { get; private set; }
    public bool AnyDestinationDeleted { get; private set; }

    // ── Ingest ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Folds one chunk in. Source files are appended before the chunk's source operations
    /// are applied, so an operation can always resolve its file: the engine emits whole per-file
    /// bundles, so an operation never references a file from a later chunk.</summary>
    public void OnChunk(DryRunChunkResponse chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (IsCompleted)
            throw new InvalidOperationException("the store is completed and can no longer ingest chunks");

        // Column to column now that the wire is columnar too, so this is close to a bulk copy — no
        // wire object is read, because none was built. The trust boundary has already rejected a chunk
        // whose columns disagree in length, so indexing them in lockstep is safe here.
        for (int i = 0; i < chunk.DirectoryName.Count; i++)
            _directories.Append(chunk.DirectoryName[i], chunk.DirectoryParentIndex[i]);

        DryRunFileColumns sourceFiles = chunk.SourceFiles;
        for (int i = 0; i < sourceFiles.Count; i++)
        {
            _srcDir.Add(sourceFiles.DirIndex[i]);
            _srcRootDir.Add(sourceFiles.RootDirIndex[i]);
            _srcName.Add(Intern(sourceFiles.FileName[i]));
            _srcSize.Add(sourceFiles.Length[i]);
            _srcKind.Add(0);
            _srcDisposition.Add(0);
            _srcDetail.Add(-1);
        }

        // Only their lengths matter — a destination operation's size is the pre-existing file it
        // touches when it carries no incoming content. Nothing else about these files is displayed,
        // so nothing else is kept, and even the lengths are dropped once Complete has folded them in.
        // Columnar makes this literal: the other five columns are never touched.
        List<long> destinationLengths = chunk.DestinationFiles.Length;
        for (int i = 0; i < destinationLengths.Count; i++)
            _destFileLength!.Add(destinationLengths[i]);

        DryRunOperationColumns sourceOps = chunk.SourceOperations;
        for (int i = 0; i < sourceOps.Count; i++)
            ApplySourceOperation(sourceOps, i, defer: true);

        DryRunOperationColumns destinationOps = chunk.DestinationOperations;
        for (int i = 0; i < destinationOps.Count; i++)
        {
            _opDir.Add(destinationOps.DirIndex[i]);
            _opRootDir.Add(destinationOps.RootDirIndex[i]);
            _opName.Add(Intern(destinationOps.FileName[i]));
            _opKind.Add((byte)destinationOps.Kind[i]);
            _opDetail.Add(DetailId(destinationOps.Detail[i]));
            _opSize.Add(0);                 // resolved in Complete, once every file list is in
            _opSource!.Add(destinationOps.SourceIndex[i]);
            _opSubject!.Add(destinationOps.SubjectIndex[i]);
        }
    }

    /// <summary>Folds one source operation onto its file's slot.
    ///
    /// <para>Out-of-range and duplicate <c>SourceIndex</c> values degrade gracefully rather than
    /// throwing and wiping the whole preview: keep the first operation for a slot, ignore a negative
    /// or nonsensical index. An index past the files seen <em>so far</em> is different, though — the
    /// engine emits whole per-file bundles, so it should not happen, but the batch projection this
    /// replaced read the assembled report and was order-independent by construction. Deferring rather
    /// than dropping keeps that property: an operation that somehow arrives before its file still
    /// lands, instead of silently leaving the row reading as an un-annotated Processed.</para></summary>
    private void ApplySourceOperation(DryRunOperationColumns ops, int index, bool defer) =>
        ApplySourceOperation(
            ops.SourceIndex[index], ops.Kind[index], ops.SourceDisposition[index],
            // Resolved to an id NOW, while the chunk's strings are still valid — a deferred op must not
            // hold a reference into a chunk the sink contract says is dead after OnChunk returns.
            DetailId(ops.Detail[index]),
            defer);

    private void ApplySourceOperation(int i, OperationKind kind, OnSuccessAction? disposition, int detailId, bool defer)
    {
        if (i < 0)
            return;
        if (i >= _srcKind.Count)
        {
            if (defer)
                (_deferredSourceOps ??= []).Add(new DeferredSourceOp(i, kind, disposition, detailId));
            return;   // still out of range after every file is in: a producer bug, dropped
        }
        if (_srcKind[i] != 0)
            return;   // first operation for this file wins
        _srcKind[i] = (byte)(kind + 1);
        _srcDisposition[i] = disposition is { } d ? (byte)(d + 1) : (byte)0;
        _srcDetail[i] = detailId;
    }

    /// <summary>A source operation that outran its file, held until <see cref="Complete"/> replays it.
    /// Values are copied out rather than referencing the chunk, which is not valid after
    /// <see cref="OnChunk"/> returns; the detail is already an id into <c>_details</c>, so nothing here
    /// pins a wire string.</summary>
    private readonly record struct DeferredSourceOp(int SourceIndex, OperationKind Kind, OnSuccessAction? Disposition, int DetailId);

    /// <summary>Deduplicates a file name against everything already ingested. A destination
    /// operation's name is almost always its source file's (a copy preserves the name), and names
    /// repeat heavily across directories besides — but every record arrives with its own freshly
    /// deserialized instance. The map is transient: <see cref="Complete"/> drops it, leaving only the
    /// one retained instance per distinct name.</summary>
    private string Intern(string name)
    {
        if (_names!.TryGetValue(name, out string? existing))
            return existing;
        _names[name] = name;
        return name;
    }

    private int DetailId(string? detail)
    {
        if (detail is null)
            return -1;
        if (_detailIds!.TryGetValue(detail, out int id))
            return id;
        id = _details.Count;
        _details.Add(detail);
        _detailIds[detail] = id;
        return id;
    }

    /// <summary>Closes ingest: resolves operation sizes, groups the operations by their source file,
    /// folds the banner counts, and works out each panel's common root. O(n) plus one pass to detect
    /// whether grouping was a no-op. After this the store is immutable and safe to read from any
    /// thread.</summary>
    public void Complete()
    {
        if (IsCompleted)
            return;

        _dirPaths = _directories.ToArray();

        // Any source operation that outran its file. Empty on every well-formed run.
        if (_deferredSourceOps is { } deferred)
        {
            foreach (DeferredSourceOp op in deferred)
                ApplySourceOperation(op.SourceIndex, op.Kind, op.Disposition, op.DetailId, defer: false);
            _deferredSourceOps = null;
        }

        int sources = _srcDir.Count;
        int ingested = _opDir.Count;
        int destinationFiles = _destFileLength!.Count;

        // Sizes first — this is the last thing that needs SubjectIndex or the destination lengths.
        for (int k = 0; k < ingested; k++)
        {
            int source = _opSource![k];
            int subject = _opSubject![k];
            _opSize[k] =
                (uint)source < (uint)sources ? _srcSize[source]
                : (uint)subject < (uint)destinationFiles ? _destFileLength[subject]
                : 0;
        }

        // Compressed-sparse-row grouping by owning source, with the no-source operations packed after
        // the grouped ones so both the per-row range and the "rows with no source" range are
        // contiguous. Operations whose SourceIndex points past the end of the source list are
        // dropped — the previous grouping never read such a key either.
        int[] offsets = new int[sources + 1];
        int noSourceCount = 0;
        for (int k = 0; k < ingested; k++)
        {
            int source = _opSource![k];
            if ((uint)source < (uint)sources) offsets[source + 1]++;
            else if (source < 0) noSourceCount++;
        }
        for (int i = 0; i < sources; i++)
            offsets[i + 1] += offsets[i];

        int grouped = offsets[sources];
        int total = grouped + noSourceCount;
        int[] order = new int[total];
        int[] cursor = new int[sources];
        int noSourceAt = grouped;
        for (int k = 0; k < ingested; k++)
        {
            int source = _opSource![k];
            if ((uint)source < (uint)sources)
                order[offsets[source] + cursor[source]++] = k;   // preserves within-source arrival order
            else if (source < 0)
                order[noSourceAt++] = k;
        }

        _targetOffset = offsets;
        OperationCount = total;
        NoSourceStart = grouped;
        _order = IsIdentity(order) ? null : order;

        // Banner counts over every ingested operation, matching the previous single pass over the
        // report's whole DestinationOperations list (including any the grouping dropped).
        for (int k = 0; k < ingested; k++)
        {
            switch ((OperationKind)_opKind[k])
            {
                case OperationKind.Overwrite: OverwriteCount++; break;
                case OperationKind.Rename: RenameCount++; break;
                case OperationKind.Deleted: AnyDestinationDeleted = true; break;
                default: break;
            }
        }
        for (int i = 0; i < sources; i++)
        {
            if (IsSourceDisposalDestructive(i))
                DisposalCount++;
        }

        SourceCommonRoot = CommonRootOf(_srcRootDir, sources);
        DestinationCommonRoot = CommonRootOf(_opRootDir, ingested);

        // Everything below has now done its whole job. Dropping the interners matters most: at the
        // cap they hold an entry per distinct name, which is comparable to the retained columns.
        _opSource = null;
        _opSubject = null;
        _destFileLength = null;
        _names = null;
        _detailIds = null;
        IsCompleted = true;
    }

    private static bool IsIdentity(int[] order)
    {
        for (int p = 0; p < order.Length; p++)
        {
            if (order[p] != p)
                return false;
        }
        return true;
    }

    /// <summary>The folder every path in a panel is shown relative to. Distinct root indices first so
    /// a 500k-file run walks a handful of roots, not one per row.</summary>
    private string? CommonRootOf(ColumnBuffer<int> rootIndices, int count)
    {
        HashSet<int> distinct = [];
        for (int i = 0; i < count; i++)
            distinct.Add(rootIndices[i]);
        List<string> roots = new(distinct.Count);
        foreach (int index in distinct)
        {
            if ((uint)index < (uint)_dirPaths.Length)
                roots.Add(_dirPaths[index]);
        }
        return DryRunPaths.CommonRoot(roots);
    }

    // ── Source-file accessors ────────────────────────────────────────────────────────────────────

    public string SourceDirPath(int index) => _dirPaths[_srcDir[index]];
    public string SourceFileName(int index) => _srcName[index];
    public string SourceRoot(int index) => _dirPaths[_srcRootDir[index]];
    public long SourceSize(int index) => _srcSize[index];

    /// <summary>The source file's disposition. A file with no source operation reads as
    /// <see cref="OperationKind.Processed"/> — the same fallback the projection used.</summary>
    public OperationKind SourceKind(int index) =>
        _srcKind[index] is 0 ? OperationKind.Processed : (OperationKind)(_srcKind[index] - 1);

    /// <summary>The deciding filter rule / unchanged reason shown on the row.</summary>
    public string? SourceDetail(int index) => DetailOf(_srcDetail[index]);

    /// <summary>What happens to the original after a successful copy, as the enum's name (null when
    /// the file does not process) — the shape the row has always exposed.</summary>
    public string? SourceDispositionText(int index) =>
        _srcDisposition[index] is 0 ? null : ((OnSuccessAction)(_srcDisposition[index] - 1)).ToString();

    /// <summary>Anything other than keeping the source is destructive from the source's view.</summary>
    public bool IsSourceDisposalDestructive(int index) =>
        _srcDisposition[index] is not 0
        && (OnSuccessAction)(_srcDisposition[index] - 1) != OnSuccessAction.KeepSource;

    public int TargetStart(int index) => _targetOffset[index];
    public int TargetEnd(int index) => _targetOffset[index + 1];

    // ── Destination-operation accessors (CSR positions) ──────────────────────────────────────────

    private int Storage(int position) => _order is null ? position : _order[position];

    public string OpDirPath(int position) => _dirPaths[_opDir[Storage(position)]];
    public string OpFileName(int position) => _opName[Storage(position)];
    public string OpRoot(int position) => _dirPaths[_opRootDir[Storage(position)]];
    public OperationKind OpKind(int position) => (OperationKind)_opKind[Storage(position)];
    public string? OpDetail(int position) => DetailOf(_opDetail[Storage(position)]);
    public long OpSize(int position) => _opSize[Storage(position)];

    private string? DetailOf(int id) => id < 0 ? null : _details[id];

    // ── Construction helpers ─────────────────────────────────────────────────────────────────────

    private static DryRunRowStore CreateEmpty()
    {
        DryRunRowStore store = new();
        store.Complete();
        return store;
    }

    /// <summary>Creates a store ready for ingest.</summary>
    public static DryRunRowStore CreateForIngest() => new();

    /// <summary>Builds a completed store from an already-assembled report — the entry point for
    /// tests, the benchmarks, and the legacy single-frame path. It runs the report through the same
    /// <see cref="OnChunk"/> ingest the stream uses, so there is one folding implementation rather
    /// than a second one that can drift from it.</summary>
    public static DryRunRowStore FromReport(DryRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        DryRunRowStore store = CreateForIngest();
        store.OnChunk(DryRunColumns.ToChunk(
            report.Directories, report.SourceFiles, report.DestinationFiles,
            report.SourceOperations, report.DestinationOperations));
        store.Complete();
        return store;
    }
}
