using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>The endless-feed store: a window onto a plan half that lives on the service, holding a
/// bounded number of pages however large the plan is.
///
/// <para><b>Why it exists.</b> <see cref="DryRunRowStore"/> holds every row — ~72 MB retained at 500,000
/// files plus a ~147 MB transient sort, both linear in the row count. That was affordable only while the
/// plan itself was capped at 500,000, and the cap is gone, because a plan is the work list a run
/// executes and bounding it produced a wrong job rather than a smaller one. This holds
/// <see cref="MaxResidentPages"/> pages instead — a few MB, flat.</para>
///
/// <para><b>A page is just a small report,</b> which is the trick that keeps this cheap: the service
/// answers a page request in the same chunk shape a preview streams, so each page is folded by the
/// <see cref="DryRunRowStore.OnChunk"/>/<see cref="DryRunRowStore.Complete"/> that already exists and
/// every row accessor comes along unchanged. There is no second ingest that could disagree with the
/// first about what a row means. Directory indices in a page are page-local by construction (the
/// service uses one converter per page), so a page store resolves its own paths and nothing has to
/// reconcile two pages' directory tables.</para>
///
/// <para><b>Misses cannot block.</b> Avalonia reads a bound list through a synchronous indexer, so a row
/// whose page is not resident yet CANNOT be waited for — that would freeze the UI thread on IPC. The
/// indexer answers immediately with whatever it has and starts a fetch;
/// <see cref="PageArrived"/> then tells the list to re-read the range. That is the ordinary
/// virtualized-feed contract, and it is why <see cref="TryGetRow"/> returns false rather than
/// blocking.</para>
///
/// <para><b>The cache is locked, not thread-affine.</b> Reads come from the UI thread and completions
/// come from wherever the await resumed — which is the UI thread in the app, because there is a
/// synchronization context, and a pool thread in a test host, because there is not. Resting the
/// integrity of a <c>HashSet</c> on an ambient context that may or may not exist is not a bargain worth
/// making for collections this small and this rarely touched, so every access takes
/// <c>_gate</c>. Events are raised outside it.</para></summary>
public sealed class PagedDryRunRowStore
{
    /// <summary>Rows per page. Matches the service's block size, so one page request maps onto whole
    /// blocks of the snapshot rather than straddling two.</summary>
    public const int PageRows = 512;

    /// <summary>Pages kept resident. At 512 rows and roughly 145 bytes a row this is ~9 MB — two orders
    /// of magnitude more than a viewport needs, so ordinary scrolling never evicts anything it is about
    /// to want back, and still flat regardless of plan size.</summary>
    public const int MaxResidentPages = 128;

    /// <summary>Pages fetched either side of the one asked for. Scrolling is sequential, so reading
    /// ahead is what keeps a placeholder off the screen in normal use; without it every page boundary
    /// would flash a "…" row while its fetch flew.</summary>
    private const int PrefetchPages = 2;

    private readonly IIpcGateway _gateway;
    private readonly Guid _runId;
    private readonly RunPlanSide _side;
    private readonly string? _viewId;

    // Insertion-ordered LRU. A Dictionary plus a recency list rather than anything cleverer because
    // MaxResidentPages is small and these are touched once per page, not once per row.
    private readonly object _gate = new();
    private readonly Dictionary<int, DryRunRowStore> _pages = [];
    private readonly LinkedList<int> _recent = new();
    private readonly Dictionary<int, LinkedListNode<int>> _recentNodes = [];
    private readonly HashSet<int> _inFlight = [];
    private readonly CancellationTokenSource _closed = new();

    private readonly string? _sourceCommonRoot;
    private readonly string? _destinationCommonRoot;

    /// <param name="sourceCommonRoot">The folder every source path is shown relative to, for the WHOLE
    /// plan — derived by the caller from the plan's roots, not from any page. A page store left to work
    /// it out from its own rows would produce a deeper root that changed as the user scrolled; see
    /// <see cref="DryRunRowStore.UseCommonRoots"/>.</param>
    public PagedDryRunRowStore(
        IIpcGateway gateway, Guid runId, RunPlanSide side, string? viewId, int rowCount,
        string? sourceCommonRoot = null, string? destinationCommonRoot = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        _runId = runId;
        _side = side;
        _viewId = viewId;
        RowCount = Math.Max(0, rowCount);
        _sourceCommonRoot = sourceCommonRoot;
        _destinationCommonRoot = destinationCommonRoot;
    }

    /// <summary>Rows in the view. Known up front from the view response, which is what lets a virtual
    /// list size its scrollbar correctly before a single row has been read.</summary>
    public int RowCount { get; }

    /// <summary>Raised when a page lands, with the row range it covers. The bound list re-reads that
    /// range; rows outside it are untouched, so a fetch completing never disturbs the rest of the
    /// view.</summary>
    public event EventHandler<(int First, int Count)>? PageArrived;

    /// <summary>Pages currently resident — for the memory assertions, which are the whole point of this
    /// type.</summary>
    public int ResidentPages
    {
        get { lock (_gate) return _pages.Count; }
    }

    /// <summary>The store holding <paramref name="row"/> and its index within it, when that page is
    /// resident. False means "not yet" — the caller shows a placeholder — and starts the fetch.
    ///
    /// <para>Never blocks and never throws for an unloaded row: the indexer behind it runs on the UI
    /// thread during layout.</para></summary>
    public bool TryGetRow(int row, out DryRunRowStore store, out int index)
    {
        store = DryRunRowStore.Empty;
        index = 0;
        if ((uint)row >= (uint)RowCount)
            return false;

        int page = row / PageRows;
        Prefetch(page);

        DryRunRowStore? resident;
        lock (_gate)
        {
            if (!_pages.TryGetValue(page, out resident))
                return false;
            Touch(page);
        }
        store = resident;
        index = row % PageRows;
        // A short page — the last one, or one the service could not fully read — must not hand back an
        // index past its rows. Reads as "not loaded", which the placeholder path already handles.
        return index < resident.SourceCount || index < resident.OperationCount;
    }

    /// <summary>Starts fetches for a page and its neighbours. Already-resident and already-in-flight
    /// pages are skipped, so scrolling does not re-request what is on its way.</summary>
    private void Prefetch(int page)
    {
        int last = (RowCount - 1) / PageRows;
        List<int>? start = null;
        lock (_gate)
        {
            for (int p = Math.Max(0, page - PrefetchPages); p <= Math.Min(last, page + PrefetchPages); p++)
            {
                if (!_pages.ContainsKey(p) && _inFlight.Add(p))
                    (start ??= []).Add(p);
            }
        }
        if (start is null)
            return;
        foreach (int p in start)
            _ = FetchAsync(p);
    }

    private async Task FetchAsync(int page)
    {
        int first = page * PageRows;
        // Never finish inside the caller. Fetches start from TryGetRow, which runs from the bound list's
        // indexer — i.e. during layout — so a gateway that answered synchronously would insert the page
        // and raise PageArrived while Avalonia was mid-read of the very list that is about to be told it
        // changed. Yielding first makes "a miss is followed by an arrival" true unconditionally, rather
        // than true only because IPC happens to be slow.
        await Task.Yield();

        try
        {
            Result<DryRunChunkResponse, IpcError> result = await _gateway.GetRunPlanPageAsync(
                new GetRunPlanPageRequest
                {
                    RunId = _runId,
                    Side = _side,
                    First = first,
                    Count = PageRows,
                    ViewId = _viewId,
                },
                _closed.Token).ConfigureAwait(true);   // resume on the UI thread — the cache is affine

            if (_closed.IsCancellationRequested || result.IsCanceled)
                return;
            if (result.TryGetError(out IpcError? error))
            {
                // A page that cannot be read leaves its placeholder in place rather than blanking the
                // list or raising a banner: the rest of the preview is intact, and a retry happens
                // naturally the next time the row is scrolled into view.
                Serilog.Log.Warning(
                    "Dry-run page {Page} of run {RunId} failed: {Code} {Message}",
                    page, _runId, error.Code, error.Message);
                return;
            }
            result.TryGetValue(out DryRunChunkResponse? chunk);

            DryRunRowStore store = DryRunRowStore.CreateForIngest(PageRows);
            store.OnChunk(chunk!);
            store.Complete();
            // AFTER Complete, which is what derives them from the page's own rows — and a page can only
            // see its own, so the plan-wide values have to replace them or every path on screen would be
            // relative to a root that shifted between pages.
            store.UseCommonRoots(_sourceCommonRoot, _destinationCommonRoot);
            Insert(page, store);
            PageArrived?.Invoke(this, (first, PageRows));
        }
        catch (OperationCanceledException)
        {
            // The preview closed while this was in flight.
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(page);
        }
    }

    private void Insert(int page, DryRunRowStore store)
    {
        lock (_gate)
        {
            _pages[page] = store;
            Touch(page);
            while (_pages.Count > MaxResidentPages && _recent.Last is { } oldest)
            {
                _pages.Remove(oldest.Value);
                _recentNodes.Remove(oldest.Value);
                _recent.RemoveLast();
            }
        }
    }

    /// <summary>Moves a page to the front of the recency list. Call under <see cref="_gate"/>.</summary>
    private void Touch(int page)
    {
        if (_recentNodes.TryGetValue(page, out LinkedListNode<int>? node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return;
        }
        _recentNodes[page] = _recent.AddFirst(page);
    }

    /// <summary>Drops every page and abandons anything in flight. Called when the preview closes or is
    /// superseded, so a fetch for the previous plan cannot land in the new one's list.</summary>
    public void Close()
    {
        _closed.Cancel();
        lock (_gate)
        {
            _pages.Clear();
            _recent.Clear();
            _recentNodes.Clear();
            _inFlight.Clear();
        }
    }
}
