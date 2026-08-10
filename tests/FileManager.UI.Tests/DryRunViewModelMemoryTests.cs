using System.Runtime.CompilerServices;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;
using Xunit.Abstractions;

namespace FileManager.UI.Tests;

/// <summary>What a populated dry-run preview holds, and — the point of the whole paging exercise —
/// whether that figure moves with the size of the plan.
///
/// <para><b>What these used to measure.</b> The preview ingested every row of a plan and sorted both
/// halves of it: ~72 MB retained at 500,000 files plus a ~147 MB transient, both linear. The probes here
/// tracked that number down across three optimization passes. They are gone because the thing they
/// measured is gone — the rows live on the service and the client holds a bounded number of pages — and
/// a budget on a path nothing takes would pass forever while saying nothing.</para>
///
/// <para><b>What replaces them is a different KIND of assertion.</b> Not "under N megabytes" but "the
/// same at 500,000 rows and at 5,000,000". A budget catches a regression in degree; this catches one in
/// kind, which is what a return to holding every row would be. Note that neither plan is ever built:
/// pages are synthesized on request, exactly as the preview never asks for more than it shows.</para>
///
/// <para>BenchmarkDotNet's Allocated column is per-op allocation, not retention, so these remain the
/// gauge for the memory work. Filter with <c>dotnet test --filter Category=Memory</c>.</para></summary>
/// <remarks>Pinned to a non-parallel collection: <see cref="GC.GetTotalMemory(bool)"/> measures the
/// whole process heap, so letting other collections allocate concurrently during a full-suite run
/// would contaminate the before/after delta and flake the budget asserts.</remarks>
[Collection("Memory")]
public sealed class DryRunViewModelMemoryTests(ITestOutputHelper output)
{
    /// <summary>The old streamed cap, kept as the reference point every earlier figure was quoted at.</summary>
    private const int ReferenceRows = 500_000;

    /// <summary>Ten times the cap — a plan the previous design could not have shown at all, and which
    /// this one must show for the same bytes.</summary>
    private const int HugeRows = 5_000_000;

    /// <summary><b>The assertion this whole change exists to make true.</b> A preview's retained heap must
    /// not track the plan's size.
    ///
    /// <para>Both figures are measured after scrolling far enough to evict pages, because a store that
    /// merely had not been asked for much yet would also look flat. If the larger plan costs materially
    /// more than the smaller one, something is accumulating per row again — which is the regression that
    /// matters, whatever the absolute number happens to be.</para></summary>
    [Fact]
    [Trait("Category", "Memory")]
    public async Task A_paged_preview_retains_the_same_heap_however_large_the_plan()
    {
        long reference = await MeasureScrolledPreviewAsync(ReferenceRows);
        long huge = await MeasureScrolledPreviewAsync(HugeRows);
        long growth = huge - reference;

        output.WriteLine(
            $"Retained with a preview open: {ReferenceRows:N0} rows → {reference / (1024.0 * 1024.0):F1} MB; " +
            $"{HugeRows:N0} rows → {huge / (1024.0 * 1024.0):F1} MB; " +
            $"growth across a 10x plan {growth / (1024.0 * 1024.0):F1} MB");

        // Flat, within noise. A linear retained cost would put the 5M figure ten times the 500k one; the
        // margin here is for GC timing and the handful of per-plan aggregates (facet keys, chip counts)
        // that are genuinely proportional to the ROOT count, not the row count.
        Assert.True(growth < 8L * 1024 * 1024,
            $"a 10x larger plan retained {growth / (1024.0 * 1024.0):F1} MB more — the preview is holding " +
            "something per row again, which is the regression paging exists to prevent");

        // And an absolute ceiling, so "flat" cannot be satisfied by both being enormous.
        //
        // History at this shape: 76.7 MB when a page store still sized its column segments for an
        // open-ended stream — 8192 elements holding 512 rows, 94% waste, multiplied by the 128 pages each
        // tab keeps resident; 17.7 MB once ColumnBuffer took the page size as a hint. The remainder is the
        // resident rows themselves plus the two tabs. Margin for GC timing, not for growth.
        Assert.True(huge < 32L * 1024 * 1024,
            $"a {HugeRows:N0}-row preview retained {huge / (1024.0 * 1024.0):F1} MB — over the 32 MB ceiling");
    }

    /// <summary>Pages resident after scrolling a plan far larger than the cache. The byte figure above is
    /// the consequence; this is the mechanism, asserted directly so a failure says which one broke.</summary>
    [Fact]
    [Trait("Category", "Memory")]
    public async Task Scrolling_a_huge_view_keeps_the_resident_page_count_bounded()
    {
        (DryRunViewModel vm, FakeIpcGateway gateway) = OpenSynthetic(HugeRows);
        await ScrollAsync(vm, HugeRows);
        await DrainAsync(gateway);

        // Every position touched was fetched at some point, so a cache that never evicted would hold
        // thousands of pages. What is asserted is that it did not.
        output.WriteLine($"Page requests during the scroll: {gateway.PageRequests.Count:N0}");
        Assert.True(gateway.PageRequests.Count > PagedDryRunRowStore.MaxResidentPages,
            "the scroll did not cross enough pages to force an eviction, so this proves nothing");

        GC.KeepAlive(vm);
    }

    /// <summary>Clearing the preview must make its pages collectable — nothing may outlive them.
    ///
    /// <para>A <see cref="WeakReference"/> rather than a byte figure, because those answer different
    /// questions: bytes cannot distinguish "that heap is garbage nobody collected yet" from "that heap is
    /// still live and there is a lifetime bug". If this fails, <c>UiMemoryTrim</c> is treating a leak and
    /// the fix is a lifetime fix instead.</para>
    ///
    /// <para>Deliberately small, and outside the Memory category: this asserts reachability, which is
    /// scale-independent.</para></summary>
    [Fact]
    public async Task Clearing_the_preview_releases_its_pages()
    {
        (DryRunViewModel vm, WeakReference page) = await OpenAndExposeAPageAsync();
        Assert.True(page.IsAlive, "a page must be resident while the preview is open, or this test proves nothing");

        vm.ClearProfile();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(page.IsAlive,
            "a resident page outlived the preview — the memory a cleared preview holds is LIVE, not " +
            "uncollected garbage, so UiMemoryTrim cannot release it and the real fix is whatever still " +
            "roots the paged store");
        GC.KeepAlive(vm);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a preview of <paramref name="rowCount"/> rows, scrolls it, and reports what the
    /// process retained. NoInlining and weak-reference-free: everything strong dies with this frame
    /// except the view model, which is what the measurement is of.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<long> MeasureScrolledPreviewAsync(int rowCount)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        (DryRunViewModel vm, FakeIpcGateway gateway) = OpenSynthetic(rowCount);
        await ScrollAsync(vm, rowCount);
        await DrainAsync(gateway);
        long after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(vm);
        return after - before;
    }

    /// <summary>Reads across the whole view in strides, which is what makes a paged store fetch and then
    /// evict. Strided rather than sequential so the walk crosses far more pages than the cache holds
    /// without reading five million rows.</summary>
    private static async Task ScrollAsync(DryRunViewModel vm, int rowCount)
    {
        const int Visits = 2_000;
        int stride = Math.Max(1, rowCount / Visits);
        for (int position = 0; position < rowCount; position += stride)
        {
            _ = vm.Sources.VisibleRows[position].FileName;
            _ = vm.Destinations.VisibleRows[position].FileName;
        }
        await DrainAsync(null);
    }

    /// <summary>Waits for the fetches the scroll started to finish. A fetch begins in the list's indexer
    /// and completes wherever the await resumed — a pool thread here, because a test host has no
    /// synchronization context — so a heap read taken immediately would count pages that are still
    /// arriving rather than the ones the cache settled on.</summary>
    private static async Task DrainAsync(FakeIpcGateway? gateway)
    {
        if (gateway is null)
        {
            await Task.Delay(100);
            return;
        }
        int seen;
        do
        {
            seen = gateway.PageRequests.Count;
            await Task.Delay(100);
        }
        while (gateway.PageRequests.Count != seen);
    }

    /// <summary>A view model showing a plan of <paramref name="rowCount"/> rows that is never built:
    /// the header aggregates are two roots and a handful of counts, and every page is synthesized when
    /// asked for. This is the only way to pose the question at five million rows — and the fact that it
    /// works at all is the property under test.</summary>
    private static (DryRunViewModel Vm, FakeIpcGateway Gateway) OpenSynthetic(int rowCount)
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            ViewResult = new RunPlanViewResponse { ViewId = null, RowCount = rowCount },
            PageFactory = SyntheticPage,
        };
        gateway.RunDetailResults[runId] = Detail(runId, rowCount);

        DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
        vm.ApplyPrepared(DryRunViewModel.PrepareReport(
            gateway, runId, Detail(runId, rowCount),
            new RunPlanViewResponse { ViewId = null, RowCount = rowCount },
            new RunPlanViewResponse { ViewId = null, RowCount = rowCount },
            new DryRunCompletion(DateTimeOffset.UnixEpoch, Truncated: false, Space: null)));
        return (vm, gateway);
    }

    /// <summary>The realistic deep-path shape the earlier probes used — ~100-char paths, 20 files per
    /// leaf directory — so a page's rows cost what a real plan's rows cost.</summary>
    private static DryRunChunkResponse SyntheticPage(GetRunPlanPageRequest request)
    {
        DryRunDirectoryTableBuilder dirs = new();
        List<DryRunFile> files = [];
        List<DryRunOperation> sourceOps = [];
        List<DryRunOperation> destinationOps = [];
        for (int i = 0; i < request.Count; i++)
        {
            int row = request.First + i;
            int leaf = row / 20;
            string name = $"render-output-{row:D7}.png";
            if (request.Side == RunPlanSide.Sources)
            {
                string path = $@"C:\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\{name}";
                files.Add(dirs.Convert(new PhysicalFile
                {
                    Path = path, Root = SourceRoot, Length = row, LastWritten = DateTimeOffset.UnixEpoch,
                }));
                DryRunOperation op = dirs.Convert(new VirtualFileOperation
                {
                    Path = path, Root = SourceRoot, Kind = OperationKind.Processed,
                    SourceIndex = files.Count - 1, SubjectIndex = -1,
                    SourceDisposition = OnSuccessAction.KeepSource,
                });
                op.TargetKinds = OperationKindMask.Bit(OperationKind.New);
                sourceOps.Add(op);
            }
            else
            {
                string path = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\{name}";
                destinationOps.Add(dirs.Convert(new VirtualFileOperation
                {
                    Path = path, Root = TargetRoot, Kind = OperationKind.New,
                    SourceIndex = -1, SubjectIndex = -1,
                }));
            }
        }
        return DryRunColumns.ToChunk(dirs.Entries.ToList(), files, [], sourceOps, destinationOps);
    }

    private const string SourceRoot = @"C:\media-archive\projects";
    private const string TargetRoot = @"D:\backup\media-archive\projects";

    private static RunDetailDto Detail(Guid runId, int rowCount) =>
        new(runId, ProfileFactory.Sample(), null, DateTimeOffset.UnixEpoch,
            CopyItemCount: rowCount, CopyBytes: 0, DeleteItemCount: 0, DeleteBytes: 0,
            SourceItemCount: rowCount, DestinationItemCount: rowCount,
            OverwriteCount: 0, RenameCount: 0, DisposalCount: 0,
            Truncated: false, SweepFaultDetail: null, Space: null)
        {
            Preview = new RunPlanPreviewAggregates
            {
                SourceRowsByRoot = new Dictionary<string, int> { [SourceRoot] = rowCount },
                DestinationRowsByRoot = new Dictionary<string, int> { [TargetRoot] = rowCount },
                DestinationRowsByKind = new Dictionary<OperationKind, int> { [OperationKind.New] = rowCount },
                UntouchedCount = 0,
                ProcessedCount = rowCount,
            },
        };

    /// <summary>NoInlining, and the page comes back only as a <see cref="WeakReference"/>, so the sole
    /// strong path to it is whatever the view model itself holds.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(DryRunViewModel Vm, WeakReference Page)> OpenAndExposeAPageAsync()
    {
        (DryRunViewModel vm, _) = OpenSynthetic(10_000);
        // Reading a row is what fetches its page; the store behind the row IS that page.
        _ = vm.Sources.VisibleRows[0].FileName;
        await Task.Delay(50);
        return (vm, new WeakReference(vm.Sources.VisibleRows[0].Store));
    }
}
