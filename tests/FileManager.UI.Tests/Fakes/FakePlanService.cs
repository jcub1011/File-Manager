using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;

namespace FileManager.UI.Tests.Fakes;

/// <summary>Serves a <see cref="DryRunReport"/> the way the service serves a run's frozen snapshot:
/// header aggregates through <c>get-run-detail</c>, filtered orders through <c>get-run-plan-view</c>, and
/// windows onto them through <c>get-run-plan-page</c>.
///
/// <para><b>Why a fake this substantial.</b> The preview no longer holds a plan — it holds a handle and a
/// page — so a test that wants to assert what a row shows has to have something on the other end of the
/// wire. Scripting individual pages by hand (which <see cref="FakeIpcGateway.Pages"/> still allows, and
/// <c>PagedDryRunRowStoreTests</c> still uses) is right for testing the paging machinery and wrong for
/// testing the view model, where the interesting question is "given THIS plan, what does the tab show".
/// This turns a report into that plan.</para>
///
/// <para><b>It is a stand-in, not a specification.</b> Where it mirrors service behaviour — the ordering
/// rule, the filter semantics, the destination half's two-segment index space — the authority is the
/// service's own tests (<c>RunSnapshotPagingTests</c>, <c>GetRunPlanViewHandlerTests</c>,
/// <c>GetRunPlanPageHandlerTests</c>), not this file. What it guarantees is that the view model is driven
/// by something shaped like the real thing rather than by hand-picked answers.</para></summary>
internal sealed class FakePlanService
{
    /// <summary>One row of one half: where it sits in its own file, and what it sorts by.</summary>
    private readonly record struct Row(int Ordinal, string Key, string Root, bool SecondSegment);

    private readonly DryRunReport _plan;
    private readonly List<Row> _sourceOrder;
    private readonly List<Row> _destinationOrder;
    private readonly Dictionary<string, IReadOnlyList<Row>> _views = [];
    private int _nextViewId;

    /// <summary>Destination operations in the order the snapshot's two files concatenate them: every
    /// non-<c>Deleted</c> projection row, then every orphan. The client sees one index space.</summary>
    private readonly List<DryRunOperation> _destinationRows = [];

    /// <summary>Per source file, the destination ops it produces — the fan-out both the source-side
    /// filter and the target-kind mask are computed over.</summary>
    private readonly Dictionary<int, List<DryRunOperation>> _fanOut = [];

    public FakePlanService(DryRunReport plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;

        foreach (DryRunOperation op in plan.DestinationOperations)
        {
            if (op.SourceIndex >= 0)
                (_fanOut.TryGetValue(op.SourceIndex, out List<DryRunOperation>? list) ? list : _fanOut[op.SourceIndex] = []).Add(op);
        }
        foreach (DryRunOperation op in plan.DestinationOperations)
        {
            if (op.Kind != OperationKind.Deleted)
                _destinationRows.Add(op);
        }
        int projectionCount = _destinationRows.Count;
        foreach (DryRunOperation op in plan.DestinationOperations)
        {
            if (op.Kind == OperationKind.Deleted)
                _destinationRows.Add(op);
        }

        Dictionary<(string, string), string> cache = [];
        _sourceOrder = Sort(
            plan.SourceFiles.Select((f, i) =>
                new Row(i, RelativeKey(Path(f), Root(f), cache), Root(f), false)));
        _destinationOrder = Sort(
            _destinationRows.Select((o, i) =>
                new Row(i, RelativeKey(Path(o), Root(o), cache), Root(o), i >= projectionCount)));
    }

    /// <summary>Total rows in a half, unfiltered — what an all-null view answers with.</summary>
    public int RowCount(RunPlanSide side) =>
        side == RunPlanSide.Sources ? _sourceOrder.Count : _destinationOrder.Count;

    // ── get-run-detail ───────────────────────────────────────────────────────────────────────────

    /// <summary>The snapshot header as the run-detail projection carries it. Every aggregate is folded
    /// under the same rules <c>RunSnapshotStore</c> applies during the plan's walk.</summary>
    public RunDetailDto Detail(Guid runId, Profile profile)
    {
        Dictionary<string, int> sourceRowsByRoot = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> destinationRowsByRoot = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<OperationKind, int> destinationRowsByKind = [];
        int untouched = 0, processed = 0, disposals = 0, overwrites = 0, renames = 0, deletes = 0;

        for (int i = 0; i < _plan.SourceFiles.Count; i++)
        {
            Bump(sourceRowsByRoot, Root(_plan.SourceFiles[i]));
            DryRunOperation? op = SourceOp(i);
            switch (op?.Kind ?? OperationKind.Processed)
            {
                case OperationKind.SkippedByFilter or OperationKind.SkippedUnchanged: untouched++; break;
                case OperationKind.Processed: processed++; break;
                default: break;
            }
            if (op?.SourceDisposition is { } disposition && disposition != OnSuccessAction.KeepSource)
                disposals++;
        }

        foreach (DryRunOperation op in _plan.DestinationOperations)
        {
            Bump(destinationRowsByRoot, Root(op));
            destinationRowsByKind[op.Kind] = destinationRowsByKind.GetValueOrDefault(op.Kind) + 1;
            switch (op.Kind)
            {
                case OperationKind.Overwrite: overwrites++; break;
                case OperationKind.Rename: renames++; break;
                case OperationKind.Deleted: deletes++; break;
                default: break;
            }
        }

        return new RunDetailDto(
            runId, profile, null, _plan.GeneratedAt,
            CopyItemCount: processed, CopyBytes: 0,
            DeleteItemCount: deletes, DeleteBytes: 0,
            SourceItemCount: _plan.SourceFiles.Count,
            DestinationItemCount: _destinationRows.Count - deletes,
            OverwriteCount: overwrites, RenameCount: renames, DisposalCount: disposals,
            Truncated: _plan.Truncated, SweepFaultDetail: null, Space: _plan.Space)
        {
            Preview = new RunPlanPreviewAggregates
            {
                SourceRowsByRoot = sourceRowsByRoot,
                DestinationRowsByRoot = destinationRowsByRoot,
                DestinationRowsByKind = destinationRowsByKind,
                UntouchedCount = untouched,
                ProcessedCount = processed,
            },
        };

        static void Bump(Dictionary<string, int> counts, string key) =>
            counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    // ── get-run-plan-view ────────────────────────────────────────────────────────────────────────

    /// <summary>Applies a filter and names the result. An all-null filter answers with a null handle and
    /// the half's own totals, at no view cost — exactly as the real handler does.</summary>
    public RunPlanViewResponse View(GetRunPlanViewRequest request)
    {
        List<Row> order = request.Side == RunPlanSide.Sources ? _sourceOrder : _destinationOrder;
        bool empty = request.Search is null
            && request.SourceRoots is null
            && request.DestinationRoots is null
            && request.Kinds is null
            && !request.IncludeDestructiveDisposition;
        if (empty)
            return new RunPlanViewResponse { ViewId = null, RowCount = order.Count };

        List<Row> kept = [.. order.Where(r => request.Side == RunPlanSide.Sources
            ? KeepSource(r.Ordinal, request)
            : KeepDestination(_destinationRows[r.Ordinal], request))];
        string viewId = $"v{_nextViewId++}";
        _views[viewId] = kept;
        return new RunPlanViewResponse { ViewId = viewId, RowCount = kept.Count };
    }

    /// <summary>Mirrors <c>RunSnapshotView.KeepSources</c>: root membership, then the ORed status test,
    /// then a search that also matches a row whose DESTINATIONS carry the term.</summary>
    private bool KeepSource(int ordinal, GetRunPlanViewRequest request)
    {
        DryRunFile file = _plan.SourceFiles[ordinal];
        DryRunOperation? op = SourceOp(ordinal);
        List<DryRunOperation> targets = _fanOut.GetValueOrDefault(ordinal) ?? [];

        if (request.SourceRoots is { } sourceRoots && !sourceRoots.Contains(Root(file), StringComparer.OrdinalIgnoreCase))
            return false;
        if (request.DestinationRoots is { } destinationRoots
            && !targets.Any(t => destinationRoots.Contains(Root(t), StringComparer.OrdinalIgnoreCase)))
            return false;
        if (request.Kinds is not null || request.IncludeDestructiveDisposition)
        {
            bool kindHit = request.Kinds is { } kinds && kinds.Contains(op?.Kind ?? OperationKind.Processed);
            bool destructiveHit = request.IncludeDestructiveDisposition
                && op?.SourceDisposition is { } d && d != OnSuccessAction.KeepSource;
            if (!kindHit && !destructiveHit)
                return false;
        }
        if (request.Search is { } term
            && !Path(file).Contains(term, StringComparison.OrdinalIgnoreCase)
            && !targets.Any(t => Path(t).Contains(term, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    /// <summary>Mirrors <c>RunSnapshotView.KeepDestinations</c>. Note what is NOT here: source root. The
    /// destination half cannot be filtered by it, which is why that facet is gone from the tab.</summary>
    private bool KeepDestination(DryRunOperation op, GetRunPlanViewRequest request)
    {
        if (request.DestinationRoots is { } roots && !roots.Contains(Root(op), StringComparer.OrdinalIgnoreCase))
            return false;
        if (request.Kinds is { } kinds && !kinds.Contains(op.Kind))
            return false;
        if (request.Search is { } term && !Path(op).Contains(term, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ── get-run-plan-page ────────────────────────────────────────────────────────────────────────

    /// <summary>One window, in the chunk shape a preview ingests. A window past the end comes back empty
    /// rather than as an error — a viewport may legally overhang while a scroll settles.</summary>
    public DryRunChunkResponse Page(GetRunPlanPageRequest request)
    {
        IReadOnlyList<Row> order =
            request.ViewId is { Length: > 0 } id && _views.TryGetValue(id, out IReadOnlyList<Row>? view)
                ? view
                : request.Side == RunPlanSide.Sources ? _sourceOrder : _destinationOrder;

        if (request.First >= order.Count || request.Count <= 0)
            return DryRunColumns.ToChunk([], [], [], [], []);
        Row[] window = [.. order.Skip(request.First).Take(request.Count)];

        // One directory table per page: indices are PAGE-LOCAL, which is what lets the client fold each
        // page into its own small store without reconciling two pages' tables.
        DryRunDirectoryTableBuilder dirs = new();
        return request.Side == RunPlanSide.Sources
            ? SourcePage(window, dirs)
            : DestinationPage(window, dirs);
    }

    private DryRunChunkResponse SourcePage(Row[] window, DryRunDirectoryTableBuilder dirs)
    {
        List<DryRunFile> files = [];
        List<DryRunOperation> ops = [];
        foreach (Row row in window)
        {
            DryRunFile source = _plan.SourceFiles[row.Ordinal];
            DryRunOperation? op = SourceOp(row.Ordinal);
            files.Add(dirs.Convert(new PhysicalFile
            {
                Path = Path(source),
                Root = Root(source),
                Length = source.Length,
                LastWritten = source.LastWritten,
                IsReparsePoint = source.IsReparsePoint,
            }));
            DryRunOperation wire = dirs.Convert(new VirtualFileOperation
            {
                Path = Path(source),
                Root = Root(source),
                Kind = op?.Kind ?? OperationKind.Unknown,
                SourceIndex = files.Count - 1,   // page-local
                SubjectIndex = -1,
                SourceDisposition = op?.SourceDisposition,
                Detail = op?.Detail,
            });
            // The recorded set of kinds this source's destinations take — the only thing a source page
            // knows about the other half, and what keeps the row's target glyph alive.
            foreach (DryRunOperation target in _fanOut.GetValueOrDefault(row.Ordinal) ?? [])
                wire.TargetKinds |= OperationKindMask.Bit(target.Kind);
            ops.Add(wire);
        }
        return DryRunColumns.ToChunk(dirs.Entries.ToList(), files, [], ops, []);
    }

    private DryRunChunkResponse DestinationPage(Row[] window, DryRunDirectoryTableBuilder dirs)
    {
        List<DryRunFile> files = [];
        List<DryRunOperation> ops = [];
        foreach (Row row in window)
        {
            DryRunOperation op = _destinationRows[row.Ordinal];
            // Every row gets a file slot, whose LENGTH is what the client shows as the destination's size.
            // For a write that is the SOURCE file's length — recorded by the snapshot writer precisely
            // because a page of this half cannot reach the other one.
            DryRunFile? existing = op.SubjectIndex >= 0 && op.SubjectIndex < _plan.DestinationFiles.Count
                ? _plan.DestinationFiles[op.SubjectIndex]
                : null;
            long size = existing?.Length ?? 0;
            if (op.SourceIndex >= 0 && op.SourceIndex < _plan.SourceFiles.Count)
                size = _plan.SourceFiles[op.SourceIndex].Length;
            files.Add(dirs.Convert(new PhysicalFile
            {
                Path = existing is null ? Path(op) : Path(existing),
                Root = Root(op),
                Length = size,
                LastWritten = existing?.LastWritten ?? default,
            }));
            ops.Add(dirs.Convert(new VirtualFileOperation
            {
                Path = Path(op),
                Root = Root(op),
                Kind = op.Kind,
                // Never the plan's source ordinal: a page does not contain the source half, and an op
                // that named one would regroup the page and break row-position identity.
                SourceIndex = -1,
                SubjectIndex = files.Count - 1,
                Detail = op.Detail,
            }));
        }
        return DryRunColumns.ToChunk(dirs.Entries.ToList(), [], files, [], ops);
    }

    // ── Ordering ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Relative-path key, then root, then segment, then ordinal — the comparator documented on
    /// <c>RunSnapshotOrder.Compare</c>. NOT a full-path compare: a full path carries its root as a
    /// prefix, so it would group by root and scatter a file away from its own destination row.</summary>
    private static List<Row> Sort(IEnumerable<Row> rows) =>
    [
        .. rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
               .ThenBy(r => r.Root, StringComparer.OrdinalIgnoreCase)
               .ThenBy(r => r.SecondSegment)
               .ThenBy(r => r.Ordinal),
    ];

    private static string RelativeKey(string path, string? root, Dictionary<(string, string), string> cache)
    {
        if (string.IsNullOrEmpty(root))
            return path;
        string dir = System.IO.Path.GetDirectoryName(path) ?? "";
        string name = System.IO.Path.GetFileName(path);
        if (!cache.TryGetValue((dir, root), out string? relDir))
            cache[(dir, root)] = relDir = System.IO.Path.GetRelativePath(root, dir);
        // "." — the file sits in the root itself, so the key is its bare name (Join would prepend ".\").
        return relDir == "." ? name : System.IO.Path.Join(relDir, name);
    }

    // ── Report accessors ─────────────────────────────────────────────────────────────────────────
    //
    // A report addresses paths through its shared directory table, so a fixture's absolute paths have to
    // be rebuilt from it. Materialized once per plan rather than per lookup.

    private string[]? _dirPaths;
    private string[] DirPaths => _dirPaths ??= DryRunDirectoryTable.Materialize(_plan.Directories);

    private string Path(DryRunFile file) => System.IO.Path.Join(DirPaths[file.DirIndex], file.FileName);
    private string Root(DryRunFile file) => DirPaths[file.RootDirIndex];
    private string Path(DryRunOperation op) => System.IO.Path.Join(DirPaths[op.DirIndex], op.FileName);
    private string Root(DryRunOperation op) => DirPaths[op.RootDirIndex];

    /// <summary>The source operation describing file <paramref name="index"/>, or null. First wins, as
    /// the store's own fold does.</summary>
    private DryRunOperation? SourceOp(int index) =>
        _plan.SourceOperations.FirstOrDefault(o => o.SourceIndex == index);
}
