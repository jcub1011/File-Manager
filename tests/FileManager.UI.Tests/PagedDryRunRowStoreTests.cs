using System.Collections;
using System.Collections.Specialized;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>The endless-feed store: a window onto a plan that lives on the service.
///
/// <para>These pin the properties the whole design rests on — that the resident footprint stays bounded
/// however large the plan is, that a row whose page has not arrived does not block the UI thread, and
/// that the bound list still looks to Avalonia like something it can index rather than something it
/// should copy. If any of those broke, the preview would quietly go back to holding everything, which is
/// the cost that stopped being bounded when the plan lost its 500,000-file cap.</para></summary>
public sealed class PagedDryRunRowStoreTests
{
    /// <summary>A page of <paramref name="count"/> source rows whose names encode their absolute row
    /// number, so a test can tell which page a row came from.
    /// <para>Its own directory builder, so the directory indices are PAGE-LOCAL — which is what the
    /// service produces (one wire converter per page) and what lets a page be folded as a standalone
    /// store with no cross-page table to reconcile.</para></summary>
    private static DryRunChunkResponse Page(int first, int count)
    {
        DryRunDirectoryTableBuilder dirs = new();
        List<DryRunFile> files = [];
        List<DryRunOperation> ops = [];
        for (int i = 0; i < count; i++)
        {
            files.Add(dirs.Convert(new PhysicalFile
            {
                Path = $@"C:\s\row-{first + i:D6}.txt",
                Root = @"C:\s",
                Length = first + i,
                LastWritten = DateTimeOffset.UnixEpoch,
            }));
            ops.Add(dirs.Convert(new VirtualFileOperation
            {
                Path = $@"C:\s\row-{first + i:D6}.txt",
                Root = @"C:\s",
                Kind = OperationKind.Processed,
                SourceIndex = i,   // page-local, exactly as the service emits it
            }));
        }
        return DryRunColumns.ToChunk(dirs.Entries, files, [], ops, []);
    }

    private static (PagedDryRunRowStore Store, FakeIpcGateway Gateway) NewStore(int rowCount)
    {
        FakeIpcGateway gateway = new();
        for (int first = 0; first < rowCount; first += PagedDryRunRowStore.PageRows)
            gateway.Pages[first] = Page(first, Math.Min(PagedDryRunRowStore.PageRows, rowCount - first));
        return (new PagedDryRunRowStore(gateway, Guid.NewGuid(), RunPlanSide.Sources, null, rowCount), gateway);
    }

    /// <summary>Spins until <paramref name="settled"/> holds, or fails. Fetch continuations land on the
    /// pool in a test host (no synchronization context), so waiting on a condition is what makes these
    /// deterministic — a fixed number of yields would be a race dressed up as a test.</summary>
    private static async Task WaitUntilAsync(Func<bool> settled, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!settled())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>Touches every row in order, letting the fetches settle — a full scroll from top to
    /// bottom.</summary>
    private static async Task ScrollThroughAsync(PagedDryRunRowStore store)
    {
        for (int row = 0; row < store.RowCount; row++)
        {
            store.TryGetRow(row, out _, out _);
            if (row % PagedDryRunRowStore.PageRows == 0)
                await Task.Delay(1);
        }
        await Task.Delay(50);
    }

    [Fact]
    public async Task Scrolling_a_huge_plan_never_holds_more_than_the_resident_page_budget()
    {
        // THE assertion. 200,000 rows is 391 pages; the store must never hold more than its budget,
        // which is what makes the footprint flat rather than linear in the plan.
        (PagedDryRunRowStore store, _) = NewStore(200_000);

        await ScrollThroughAsync(store);

        Assert.True(
            store.ResidentPages <= PagedDryRunRowStore.MaxResidentPages,
            $"held {store.ResidentPages} pages, above the {PagedDryRunRowStore.MaxResidentPages} budget");
    }

    [Fact]
    public async Task A_row_reads_back_the_value_its_page_carries()
    {
        (PagedDryRunRowStore store, _) = NewStore(2_000);

        // Warm the page holding row 1,500, then read it.
        store.TryGetRow(1_500, out _, out _);
        await WaitUntilAsync(() => store.TryGetRow(1_500, out _, out _), "row 1,500's page");

        Assert.True(store.TryGetRow(1_500, out DryRunRowStore page, out int within));
        Assert.Equal(@"row-001500.txt", page.SourceFileName(within));
    }

    [Fact]
    public void An_unloaded_row_reports_a_miss_instead_of_blocking()
    {
        // The indexer behind this runs on the UI thread during layout, so a miss has to answer
        // immediately. A version that waited would freeze the window on every scroll.
        //
        // The gate holds every fetch open, which is what makes "not arrived yet" a state the test can
        // actually observe rather than one it has to win a race to catch.
        (PagedDryRunRowStore store, FakeIpcGateway gateway) = NewStore(10_000);
        gateway.PageGate = new TaskCompletionSource();

        Assert.False(store.TryGetRow(9_000, out _, out _));
    }

    [Fact]
    public async Task Rows_past_the_end_are_a_miss_rather_than_a_throw()
    {
        (PagedDryRunRowStore store, _) = NewStore(600);
        await ScrollThroughAsync(store);

        Assert.False(store.TryGetRow(600, out _, out _));
        Assert.False(store.TryGetRow(-1, out _, out _));
    }

    [Fact]
    public async Task A_page_is_fetched_once_however_many_of_its_rows_are_touched()
    {
        (PagedDryRunRowStore store, FakeIpcGateway gateway) = NewStore(512);

        for (int row = 0; row < 512; row++)
            store.TryGetRow(row, out _, out _);
        await WaitUntilAsync(() => store.ResidentPages > 0, "the only page to land");

        Assert.Equal(1, gateway.PageRequests.Count(r => r.First == 0));
    }

    [Fact]
    public async Task Closing_the_store_drops_its_pages()
    {
        // A superseded preview must not keep a plan's worth of rows alive, and a fetch still in flight
        // must not land in the next preview's list.
        (PagedDryRunRowStore store, _) = NewStore(5_000);
        store.TryGetRow(0, out _, out _);
        await WaitUntilAsync(() => store.ResidentPages > 0, "the first page to land");

        store.Close();

        Assert.Equal(0, store.ResidentPages);
    }

    // ── The bound list ───────────────────────────────────────────────────────────────────────────

    private static PagedDryRunRowList<string> ListOver(PagedDryRunRowStore store) =>
        new(store,
            (page, within, _) => page.SourceFileName(within),
            position => $"<pending {position}>");

    [Fact]
    public void The_list_implements_the_non_generic_IList()
    {
        // Load-bearing, exactly as for DryRunRowList: Avalonia's ItemsSourceView falls back to COPYING
        // the whole source into a list when this is missing — which here would try to materialize every
        // row of a plan that deliberately is not resident.
        (PagedDryRunRowStore store, _) = NewStore(100_000);
        PagedDryRunRowList<string> list = ListOver(store);

        Assert.IsAssignableFrom<IList>(list);
        Assert.Equal(100_000, ((IList)list).Count);
    }

    [Fact]
    public void An_unloaded_position_renders_as_a_placeholder()
    {
        (PagedDryRunRowStore store, FakeIpcGateway gateway) = NewStore(10_000);
        gateway.PageGate = new TaskCompletionSource();
        PagedDryRunRowList<string> list = ListOver(store);

        Assert.Equal("<pending 9000>", list[9_000]);
    }

    [Fact]
    public async Task A_landed_page_replaces_only_its_own_rows()
    {
        // Replace over the range, not Reset: a Reset makes the list re-read everything and drops scroll
        // position and selection, which on a paged list would also re-trigger fetches for pages the
        // user has already scrolled past.
        (PagedDryRunRowStore store, _) = NewStore(5_000);
        PagedDryRunRowList<string> list = ListOver(store);
        List<NotifyCollectionChangedEventArgs> changes = [];
        list.CollectionChanged += (_, e) => changes.Add(e);

        _ = list[0];   // a miss, which starts the fetch
        await WaitUntilAsync(() => changes.Count > 0, "the first page to land");

        Assert.NotEmpty(changes);
        Assert.All(changes, e => Assert.Equal(NotifyCollectionChangedAction.Replace, e.Action));
        Assert.Contains(changes, e => e.NewStartingIndex == 0);
        Assert.Equal("row-000000.txt", list[0]);
    }

    [Fact]
    public async Task Detaching_stops_the_list_hearing_about_later_pages()
    {
        (PagedDryRunRowStore store, _) = NewStore(5_000);
        PagedDryRunRowList<string> list = ListOver(store);
        List<NotifyCollectionChangedEventArgs> changes = [];
        list.CollectionChanged += (_, e) => changes.Add(e);

        list.Detach();
        _ = list[2_000];
        await WaitUntilAsync(() => store.ResidentPages > 0, "the page to land regardless");

        Assert.Empty(changes);
    }
}
