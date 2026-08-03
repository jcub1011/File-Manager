using System.Diagnostics;
using System.Runtime.CompilerServices;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.ViewModels;
using Xunit.Abstractions;

namespace FileManager.UI.Tests;

/// <summary>Measures what <see cref="DryRunViewModel.ApplyReport"/> leaves on the retained heap at
/// the engine's <c>MaxStreamedFiles</c> cap (500k source files) — the number a populated dry-run
/// preview holds for its lifetime. BenchmarkDotNet's Allocated column is per-op allocation, not
/// retention, so this probe is the gauge for the memory-optimization work. Filter with
/// <c>dotnet test --filter Category=Memory</c>; it builds a 500k-file report and is slow.</summary>
/// <remarks>Pinned to a non-parallel collection: <see cref="GC.GetTotalMemory(bool)"/> measures the
/// whole process heap, so letting other collections allocate concurrently during a full-suite run
/// would contaminate the before/after delta and flake the budget asserts.</remarks>
[Collection("Memory")]
public sealed class DryRunViewModelMemoryTests(ITestOutputHelper output)
{
    private const int FileCount = 500_000;

    [Fact]
    [Trait("Category", "Memory")]
    public void ApplyReport_retained_heap_at_streamed_cap()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        (DryRunViewModel vm, WeakReference report) = BuildAndApply();
        long after = GC.GetTotalMemory(forceFullCollection: true);

        // The delta is meaningless unless the report itself was collected — otherwise it counts
        // report + rows instead of the rows the preview actually retains.
        Assert.False(report.IsAlive);

        long retained = after - before;
        output.WriteLine($"Retained after ApplyReport({FileCount:N0} files): {retained:N0} bytes ({retained / (1024.0 * 1024.0):F1} MB)");

        // Budget guard. History at this shape/count: 372 MB before any optimization; 250 MB after
        // lazy display strings + root interning; 238 MB after the directory-table report (this
        // shallow shape's ~28-char paths understate that step — see the deep-path probe below);
        // 66 MB once rows became (store, index) handles over the columnar DryRunRowStore, which
        // removed the object per file AND the second materialization of every destination operation.
        // The margin catches a regression back toward an object per row without flaking on GC noise.
        Assert.True(retained < 95L * 1024 * 1024,
            $"ApplyReport retained {retained / (1024.0 * 1024.0):F1} MB — over the 95 MB budget");

        GC.KeepAlive(vm);   // rows must outlive the second measurement
    }

    /// <summary>The benchmark shape above is unrealistically shallow (64 directories, ~28-char
    /// paths), which understates what the directory-table report buys: there a filename is nearly
    /// as long as the whole path. This variant uses a realistic tree — ~25k directories, ~100-char
    /// paths — where per-row path bytes dominated the old flat-string model. History at this shape:
    /// 519 MB before any optimization, 307 MB after lazy display strings + interned roots, 219 MB
    /// with the directory-table report — whose property this budget pins: retained heap no longer
    /// scales with path depth — and 72 MB once rows became handles over the columnar store.
    /// <para>Note this shape now measures HIGHER than the shallow one (72 vs 66 MB), the reverse of
    /// before. That is the expected end state: with an object per row gone, what is left is dominated
    /// by the interned file names and the directory-path table, and this shape has ~25k directories
    /// with ~100-char paths against the other's 64.</para></summary>
    [Fact]
    [Trait("Category", "Memory")]
    public void ApplyReport_retained_heap_at_streamed_cap_with_realistic_deep_paths()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        (DryRunViewModel vm, WeakReference report) = BuildAndApplyDeep();
        long after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.False(report.IsAlive);

        long retained = after - before;
        output.WriteLine($"Retained after ApplyReport({FileCount:N0} deep-path files): {retained:N0} bytes ({retained / (1024.0 * 1024.0):F1} MB)");

        Assert.True(retained < 100L * 1024 * 1024,
            $"ApplyReport retained {retained / (1024.0 * 1024.0):F1} MB — over the 100 MB budget");

        GC.KeepAlive(vm);
    }

    /// <summary>What turning on tree view adds on top of the populated preview — the forest of
    /// directory <see cref="DryRunTreeNode"/>s plus the TreeDataGrid sources — and how long the build
    /// takes, at the streamed cap on the realistic deep-path shape. Past the sync-rebuild threshold the
    /// build runs on the thread pool (the UI thread no longer freezes), so each toggle awaits its
    /// <c>PendingRebuild</c> before the clock stops. Headless there is no grid, so no directory is
    /// expanded and file leaves are never materialized — this measures the up-front (collapsed) forest
    /// cost, which lazy leaf materialization is designed to minimize.</summary>
    [Fact]
    [Trait("Category", "Memory")]
    public async Task Tree_toggle_retained_heap_and_build_time_at_streamed_cap_with_realistic_deep_paths()
    {
        (DryRunViewModel vm, WeakReference report) = BuildAndApplyDeep();

        long before = GC.GetTotalMemory(forceFullCollection: true);
        Assert.False(report.IsAlive);

        Stopwatch watch = Stopwatch.StartNew();
        vm.Sources.ShowTree = true;
        await vm.Sources.PendingRebuild;
        long sourcesMs = watch.ElapsedMilliseconds;
        vm.Destinations.ShowTree = true;
        await vm.Destinations.PendingRebuild;
        watch.Stop();

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long retained = after - before;
        output.WriteLine(
            $"Tree toggle (both tabs, {FileCount:N0} deep-path files): retained {retained:N0} bytes " +
            $"({retained / (1024.0 * 1024.0):F1} MB); Sources build {sourcesMs:N0} ms, both tabs {watch.ElapsedMilliseconds:N0} ms");

        // Budget guard. History at this shape/count: 681 MB / ~4.8 s when BuildForest split every
        // row's absolute path (one node per file, each retaining its own full path, counts
        // dictionary, and pill strings); 116 MB / ~1.6 s building from the shared directory
        // structure (leaves reference their rows' strings; pills memoized); 48 MB / ~0.6 s once file
        // leaves became lazy — only directory nodes + per-directory row buckets are built up front,
        // so the collapsed forest no longer carries a node per file; 48 MB once the forest was built
        // over row INDICES, making each per-directory bucket 4 bytes a file instead of a row
        // reference. Time is reported but not asserted — wall clock flakes across machines.
        Assert.True(retained < 60L * 1024 * 1024,
            $"Tree toggle retained {retained / (1024.0 * 1024.0):F1} MB — over the 60 MB budget");

        GC.KeepAlive(vm);
    }

    /// <summary>How much of the retained store is <em>file names</em>, measured rather than derived.
    /// Builds the deep store twice — once normally, once with every file name replaced by one shared
    /// constant so the store's interner collapses them to a single instance — and diffs the retained
    /// heap. Everything else (row count, directory table, detail strings, every fixed-width column) is
    /// byte-identical between the two, so the delta is the all-in name cost: the distinct
    /// <see cref="string"/> objects plus the two reference columns that point at them.
    ///
    /// <para>This exists because <c>docs/dry-run-ui-memory-next-steps.md</c> §"Question 2" is
    /// arithmetic, not measurement, and it is the number that decides whether a UTF-8 name blob is
    /// worth the API churn. Its estimate was ~38 MB (names only); attributing the <c>_srcName</c> and
    /// <c>_opName</c> reference columns to names as well predicts ~46.7 MB of the deep shape's
    /// ~72 MB. Reported, not asserted — this is a gauge for a decision, not an invariant.</para></summary>
    [Fact]
    [Trait("Category", "Memory")]
    public void File_names_share_of_retained_store_at_streamed_cap_with_realistic_deep_paths()
    {
        long withDistinctNames = MeasureDeepStore(fixedName: null);
        long withOneSharedName = MeasureDeepStore(fixedName: "render-output-0000000.png");
        long nameCost = withDistinctNames - withOneSharedName;

        output.WriteLine(
            $"Deep store retained ({FileCount:N0} files): {withDistinctNames / (1024.0 * 1024.0):F1} MB with distinct names, " +
            $"{withOneSharedName / (1024.0 * 1024.0):F1} MB with one shared name — " +
            $"file names cost {nameCost / (1024.0 * 1024.0):F1} MB " +
            $"({100.0 * nameCost / withDistinctNames:F0}% of the store)");

        // The only thing worth failing on: the two builds must actually differ in name storage, or the
        // probe is measuring nothing (e.g. a future change that stops interning, or a builder edit that
        // ignores the override).
        Assert.True(nameCost > 0,
            $"the shared-name build retained no less than the distinct-name build ({nameCost:N0} bytes) — the probe is not measuring name cost");
    }

    /// <summary>The <em>transient</em> cost of turning a completed store into both tabs' loads — the
    /// gauge this suite was missing. The three probes above all measure retained heap after a forced
    /// full collection, so by construction none of them can see the allocation burst
    /// <see cref="DryRunViewModel.PrepareReport"/> pays: each tab's <c>ComputeLoad</c> materializes a
    /// <c>string[] keys</c> holding one relative-path string per row, sorts an index array against it,
    /// and drops it — and the two tabs run in parallel, so both arrays are live at once.
    ///
    /// <para>On this deep shape a key is ~71 chars (~168 B), which predicts ~84 MB for Sources plus
    /// ~56 MB for Destinations — roughly double the entire retained heap, and the number a user is most
    /// likely to actually notice, because it lands while they are watching the progress bar. The
    /// committed <c>DryRunViewModelBenchmarks</c> figure (557 ms / 71,783 KB) is the <em>shallow</em>
    /// shape, where keys are ~21 chars, so it understates this by more than half.</para>
    ///
    /// <para>Allocated bytes are exact and deterministic; the peak managed figure is polled from a
    /// background thread and depends on GC timing, so it is reported and not asserted. The budget is
    /// deliberately loose — it exists to catch an order-of-magnitude regression, not to pin a
    /// number.</para></summary>
    [Fact]
    [Trait("Category", "Memory")]
    public void PrepareReport_transient_allocation_at_streamed_cap_with_realistic_deep_paths()
    {
        (DryRunRowStore store, WeakReference report) = BuildDeepStore(fixedName: null);
        // Settle first, THEN assert: the ingest report is unreachable but not yet collected, and this
        // probe's whole subject is the transient delta above the settled baseline — so the baseline has
        // to exclude the report's bytes or the peak below is measured against the wrong floor.
        long settled = GC.GetTotalMemory(forceFullCollection: true);
        Assert.False(report.IsAlive);
        long peakManaged = settled;
        using CancellationTokenSource pollStop = new();
        Thread poll = new(() =>
        {
            while (!pollStop.IsCancellationRequested)
            {
                long sample = GC.GetTotalMemory(false);
                if (sample > peakManaged)
                    Interlocked.Exchange(ref peakManaged, sample);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        poll.Start();

        long allocatedBefore = GC.GetTotalAllocatedBytes();
        Stopwatch watch = Stopwatch.StartNew();
        DryRunViewModel.PreparedReport prepared = DryRunViewModel.PrepareReport(
            store, new DryRunCompletion(DateTimeOffset.UnixEpoch, Truncated: false, Space: null));
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
        pollStop.Cancel();
        poll.Join();

        output.WriteLine(
            $"PrepareReport({FileCount:N0} deep-path files): allocated {allocated:N0} bytes " +
            $"({allocated / (1024.0 * 1024.0):F1} MB) in {watch.ElapsedMilliseconds:N0} ms; " +
            $"managed heap {settled / (1024.0 * 1024.0):F1} MB before → peak {peakManaged / (1024.0 * 1024.0):F1} MB " +
            $"(+{(peakManaged - settled) / (1024.0 * 1024.0):F1} MB transient)");

        Assert.True(allocated < 400L * 1024 * 1024,
            $"PrepareReport allocated {allocated / (1024.0 * 1024.0):F1} MB — over the 400 MB budget");

        GC.KeepAlive(prepared);
        GC.KeepAlive(store);
    }

    /// <summary>How many bytes survive clearing a preview at the cap, with both trees having been
    /// toggled on first — the strongest version of the question
    /// <see cref="Clearing_the_preview_releases_the_row_store"/> only asks about the store.
    ///
    /// <para>Why it exists: on the published exe the trim drops committed 204 → 62 MB and it stays down,
    /// but 62 MB of managed heap remains against an 8 MB startup idle. This probe answers where that
    /// belongs. It measures <strong>0.3 MB</strong> — the view models release the entire preview, store
    /// and forest included, at the cap and with both trees built. So the shipped exe's residual is UI
    /// layer (Avalonia realized containers, TreeDataGrid rows, text/glyph and font-atlas caches), none
    /// of which headless has, and none of which is preview data. It also explains the second run's
    /// higher peak without any retention: run 2 starts from that raised floor, not from startup idle.</para>
    ///
    /// <para><strong>Read the helper's comment before touching this.</strong> An earlier version awaited
    /// the tree rebuilds in the test body and reported 94 MB / 78% retained, which was the probe
    /// measuring its own state machine rather than the app. Reachability is asserted alongside the byte
    /// figure here specifically so that failure mode is visible rather than convincing.</para></summary>
    /// <param name="withTree">Both variants matter, and running them as a pair is the bisect: the flat
    /// list and the tree forest are separate retention paths, and knowing which one holds is the
    /// difference between a one-line fix and a hunt.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Memory")]
    public async Task Clearing_the_preview_returns_the_retained_heap_at_streamed_cap(bool withTree)
    {
        long idle = GC.GetTotalMemory(forceFullCollection: true);
        // The tree toggles are awaited inside a NoInlining helper on purpose. Awaiting RebuildAsync in
        // THIS frame would leave the test's own state machine holding the awaited Task — and an
        // `async Task` method's state machine lives inside its Task object, so that alone pins the
        // RebuildInput (store) and RebuildResult (forest) and the probe would measure its own scaffolding
        // instead of the app.
        (DryRunViewModel vm, WeakReference report, WeakReference store, WeakReference? forestRoot) =
            await BuildApplyAndMaybeShowTrees(withTree);
        long loaded = GC.GetTotalMemory(forceFullCollection: true);
        Assert.False(report.IsAlive);

        // ClearProfile runs UiMemoryTrim's two aggressive compacting passes itself, so what follows is
        // measured after exactly what the app does at this moment.
        vm.ClearProfile();
        long cleared = GC.GetTotalMemory(forceFullCollection: true);
        long residual = cleared - idle;
        GCMemoryInfo info = GC.GetGCMemoryInfo();

        output.WriteLine(
            $"Clear at cap ({FileCount:N0} deep-path files, trees {(withTree ? "built" : "never toggled")}): " +
            $"idle {idle / (1024.0 * 1024.0):F1} MB → loaded {loaded / (1024.0 * 1024.0):F1} MB → " +
            $"cleared {cleared / (1024.0 * 1024.0):F1} MB; residual above idle {residual / (1024.0 * 1024.0):F1} MB " +
            $"({100.0 * residual / (loaded - idle):F0}% of the preview held on to); " +
            $"GC heap {info.HeapSizeBytes / (1024.0 * 1024.0):F1} MB, committed {info.TotalCommittedBytes / (1024.0 * 1024.0):F1} MB, " +
            $"fragmented {info.FragmentedBytes / (1024.0 * 1024.0):F1} MB; " +
            $"store {(store.IsAlive ? "STILL REACHABLE" : "released")}, " +
            $"forest {(withTree ? forestRoot!.IsAlive ? "STILL REACHABLE" : "released" : "n/a")}");

        Assert.False(store.IsAlive, "the row store is still reachable after clearing the preview");
        if (forestRoot is { } root)
            Assert.False(root.IsAlive, "the tree forest is still reachable after clearing the preview");
        Assert.True(residual < 8L * 1024 * 1024,
            $"clearing the preview left {residual / (1024.0 * 1024.0):F1} MB above idle — the view models are " +
            "retaining part of the preview, so a second run starts from a raised floor");

        GC.KeepAlive(vm);
    }

    /// <summary>Clearing the preview must make the whole row store collectable — nothing may outlive it.
    ///
    /// <para>This is the test that settles what the shipped-exe log could not. Measured there
    /// (<c>docs/dry-run-ui-memory-next-steps.md</c> finding 8): ten seconds after a clear the process
    /// still held 214 MB against a 108 MB idle, with the gen2 counter unmoved — which is ambiguous
    /// between "that heap is garbage nobody collected" and "that heap is still live, and there is a
    /// lifetime bug". A log cannot distinguish them; a <see cref="WeakReference"/> can. If this test
    /// fails, <c>UiMemoryTrim</c> is treating a leak, and the fix is a lifetime fix instead.</para>
    ///
    /// <para>Deliberately small (10k files, not the 500k cap): this asserts reachability, which is
    /// scale-independent, and it is the one probe here worth running outside the Memory category.</para></summary>
    [Fact]
    public void Clearing_the_preview_releases_the_row_store()
    {
        (DryRunViewModel vm, WeakReference store) = ApplyThenExposeStore(10_000);
        Assert.True(store.IsAlive, "the store must be alive while the preview is loaded, or this test proves nothing");

        vm.ClearProfile();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(store.IsAlive,
            "the row store is still reachable after clearing the preview — the memory a cleared preview " +
            "holds is LIVE, not uncollected garbage, so UiMemoryTrim cannot release it and the real fix " +
            "is whatever still roots the store");
        GC.KeepAlive(vm);
    }

    /// <summary>Which of the tree's parts survives a clear. Splits the 94 MB residual
    /// <see cref="Clearing_the_preview_returns_the_retained_heap_at_streamed_cap"/> reports into named
    /// suspects, so the fix targets a root rather than a symptom. Small fixture — reachability does not
    /// depend on scale.</summary>
    /// <param name="fileCount">Straddles <c>DryRunRebuild.SyncThreshold</c> (5,000) deliberately: below
    /// it the rebuild runs inline, above it it hops to the thread pool via <c>Task.Run</c>, and those are
    /// different retention paths.</param>
    [Theory]
    [InlineData(4_000)]
    [InlineData(20_000)]
    public async Task Clearing_the_preview_releases_the_tree_forest(int fileCount)
    {
        (DryRunViewModel vm, WeakReference store, WeakReference[] parts) =
            await ApplyWithTreeThenExposeParts(fileCount);
        Assert.All(parts, p => Assert.True(p.IsAlive, "every part must be alive while the trees are shown, or this test proves nothing"));

        vm.ClearProfile();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        string[] names = ["Sources forest", "Sources grid source", "Destinations forest", "Destinations grid source"];
        string[] survivors = [.. names.Where((_, i) => parts[i].IsAlive)];
        Assert.True(survivors.Length == 0, $"these outlived the clear: {string.Join(", ", survivors)}");
        Assert.False(store.IsAlive, "the row store outlived the clear (a forest's leaf factories hold it)");
        GC.KeepAlive(vm);
    }

    /// <summary>Both tabs, because they build their forests through different selectors
    /// (<c>SourceDirPath</c>/<c>SourceFileName</c> vs <c>OpDirPath</c>/<c>OpFileName</c> over CSR
    /// positions) and are therefore separate retention paths.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(DryRunViewModel Vm, WeakReference Store, WeakReference[] Parts)>
        ApplyWithTreeThenExposeParts(int fileCount)
    {
        DryRunReport report = BuildDeepReport(fileCount);
        DryRunRowStore store = DryRunRowStore.FromReport(report);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyPrepared(DryRunViewModel.PrepareReport(
            store, new DryRunCompletion(DateTimeOffset.UnixEpoch, Truncated: false, Space: null)));
        vm.Sources.ShowTree = true;
        await vm.Sources.PendingRebuild;
        vm.Destinations.ShowTree = true;
        await vm.Destinations.PendingRebuild;
        WeakReference[] parts =
        [
            new(vm.Sources.Tree[0]), new(vm.Sources.TreeSource),
            new(vm.Destinations.Tree[0]), new(vm.Destinations.TreeSource),
        ];
        return (vm, new WeakReference(store), parts);
    }

    /// <summary>NoInlining, and the store is returned only as a <see cref="WeakReference"/>, so the sole
    /// strong path to it is whatever the view model itself holds.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunViewModel Vm, WeakReference Store) ApplyThenExposeStore(int fileCount)
    {
        DryRunReport report = BuildDeepReport(fileCount);
        DryRunRowStore store = DryRunRowStore.FromReport(report);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyPrepared(DryRunViewModel.PrepareReport(
            store, new DryRunCompletion(DateTimeOffset.UnixEpoch, Truncated: false, Space: null)));
        return (vm, new WeakReference(store));
    }

    /// <summary>Retained heap of a completed store alone (no view model, no tabs), so a name-storage
    /// change can be attributed without the tabs' key arrays in the way. NoInlining, and the store dies
    /// with this frame so the next call's forced collection starts from a clean baseline.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDeepStore(string? fixedName)
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        (DryRunRowStore store, WeakReference report) = BuildDeepStore(fixedName);
        long after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.False(report.IsAlive);
        GC.KeepAlive(store);
        return after - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunRowStore Store, WeakReference Report) BuildDeepStore(string? fixedName)
    {
        DryRunReport report = BuildDeepReport(FileCount, fixedName);
        DryRunRowStore store = DryRunRowStore.FromReport(report);
        return (store, new WeakReference(report));
    }

    /// <summary>NoInlining so the report reference provably dies with this frame — nulling a local
    /// in the caller would not guarantee the JIT treats it as unreachable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunViewModel Vm, WeakReference Report) BuildAndApply()
    {
        DryRunReport report = BuildReport(FileCount);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyReport(report);
        return (vm, new WeakReference(report));
    }

    /// <summary>Builds, applies and optionally shows both trees, returning only weak references — so
    /// every strong path to the store and the forest is one the view model itself holds, and the awaited
    /// rebuild Tasks die with this frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(DryRunViewModel Vm, WeakReference Report, WeakReference Store, WeakReference? ForestRoot)>
        BuildApplyAndMaybeShowTrees(bool withTree)
    {
        (DryRunViewModel vm, WeakReference report, WeakReference store) = BuildAndApplyDeepTracked();
        if (!withTree)
            return (vm, report, store, null);
        vm.Sources.ShowTree = true;
        await vm.Sources.PendingRebuild;
        vm.Destinations.ShowTree = true;
        await vm.Destinations.PendingRebuild;
        return (vm, report, store, new WeakReference(vm.Sources.Tree[0]));
    }

    /// <summary>As <see cref="BuildAndApplyDeep"/>, but also weak-references the store the view model
    /// built internally, so a probe can report byte residual and reachability from the same run rather
    /// than inferring across two tests.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunViewModel Vm, WeakReference Report, WeakReference Store) BuildAndApplyDeepTracked()
    {
        DryRunReport report = BuildDeepReport(FileCount);
        DryRunRowStore store = DryRunRowStore.FromReport(report);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyPrepared(DryRunViewModel.PrepareReport(
            store, new DryRunCompletion(report.GeneratedAt, Truncated: false, Space: null)));
        return (vm, new WeakReference(report), new WeakReference(store));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunViewModel Vm, WeakReference Report) BuildAndApplyDeep()
    {
        DryRunReport report = BuildDeepReport(FileCount);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyReport(report);
        return (vm, new WeakReference(report));
    }

    /// <summary>A realistic 500k-file tree: 20 files per leaf directory, leaves nested four levels
    /// under the root (project/assets/renders/batch), absolute paths ~100 chars. Ops mirror the
    /// benchmark shape's disposition mix (processed with two targets / filtered / unchanged).</summary>
    /// <param name="fixedName">When set, every file uses this name instead of a distinct one. Rows,
    /// directories, operations and detail strings are unchanged — only the store's name interner
    /// collapses, from one instance per file to one overall. Diffing the retained heap against a
    /// <c>null</c> build therefore isolates the all-in cost of file names; see
    /// <see cref="File_names_share_of_retained_store_at_streamed_cap_with_realistic_deep_paths"/>.
    /// The default distinct name is 26 chars, so pass a 26-char constant to keep the one surviving
    /// string the same size and the delta purely a count difference.</param>
    private static DryRunReport BuildDeepReport(int fileCount, string? fixedName = null)
    {
        DryRunDirectoryTableBuilder dirs = new();
        var sourceFiles = new List<DryRunFile>(fileCount);
        var sourceOps = new List<DryRunOperation>(fileCount);
        var destinationFiles = new List<DryRunFile>();
        var destinationOps = new List<DryRunOperation>();

        for (int i = 0; i < fileCount; i++)
        {
            int leaf = i / 20;                    // 20 files per directory → 25k leaf dirs at 500k
            string sourceDir = $@"C:\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}";
            string name = fixedName ?? $"render-output-{i:D7}.png";
            string source = $@"{sourceDir}\{name}";
            sourceFiles.Add(dirs.Convert(new PhysicalFile
            {
                Path = source,
                Root = @"C:\media-archive\projects",
                Length = i,
                LastWritten = DateTimeOffset.UnixEpoch,
            }));

            switch (i % 3)
            {
                case 0:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\media-archive\projects",
                        Kind = OperationKind.Processed,
                        SourceIndex = i,
                        SourceDisposition = i % 6 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource,
                    }));
                    string target = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\{name}";
                    destinationOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = target, Root = @"D:\backup\media-archive\projects", Kind = OperationKind.New, SourceIndex = i,
                    }));
                    break;

                case 1:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\media-archive\projects",
                        Kind = OperationKind.SkippedByFilter,
                        SourceIndex = i,
                        Detail = "exclude *.tmp",
                    }));
                    break;

                default:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\media-archive\projects",
                        Kind = OperationKind.SkippedUnchanged,
                        SourceIndex = i,
                    }));
                    string unchanged = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\{name}";
                    int unchangedSubject = destinationFiles.Count;
                    destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = unchanged, Root = @"D:\backup\media-archive\projects", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                    destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = unchanged, Root = @"D:\backup\media-archive\projects", Kind = OperationKind.SkipUnchanged, SourceIndex = i, SubjectIndex = unchangedSubject, Detail = "identical content (SHA-256)" }));
                    break;
            }
        }

        return new DryRunReport
        {
            ProfileId = Guid.NewGuid(),
            GeneratedAt = DateTimeOffset.UtcNow,
            Directories = dirs.Entries.ToList(),
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
        };
    }

    /// <summary>Same report shape as <c>DryRunViewModelBenchmarks.BuildReport</c> so the two gauges
    /// measure the same workload: files spread across the three source dispositions, processed rows
    /// fanning out to two targets with a mix of operation kinds.</summary>
    private static DryRunReport BuildReport(int fileCount)
    {
        DryRunDirectoryTableBuilder dirs = new();
        var sourceFiles = new List<DryRunFile>(fileCount);
        var sourceOps = new List<DryRunOperation>(fileCount);
        var destinationFiles = new List<DryRunFile>();
        var destinationOps = new List<DryRunOperation>();

        for (int i = 0; i < fileCount; i++)
        {
            string source = $@"C:\src\dir-{i % 64}\file-{i}.dat";
            sourceFiles.Add(dirs.Convert(new PhysicalFile
            {
                Path = source,
                Root = @"C:\src",
                Length = i,
                LastWritten = DateTimeOffset.UnixEpoch,
            }));

            switch (i % 3)
            {
                case 0:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.Processed,
                        SourceIndex = i,
                        SourceDisposition = i % 6 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource,
                    }));

                    string firstTarget = $@"C:\dst\file-{i}.dat";
                    if (i % 4 == 0)
                    {
                        int subject = destinationFiles.Count;
                        destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = firstTarget, Root = @"C:\dst", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = firstTarget, Root = @"C:\dst", Kind = OperationKind.Overwrite, SourceIndex = i, SubjectIndex = subject, Detail = "existing file" }));
                    }
                    else
                    {
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = firstTarget, Root = @"C:\dst", Kind = OperationKind.New, SourceIndex = i }));
                    }

                    if (i % 5 == 0)
                    {
                        string original = $@"C:\dst2\file-{i}.dat";
                        int subject = destinationFiles.Count;
                        destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = original, Root = @"C:\dst2", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = $@"C:\dst2\file-{i} (1).dat", Root = @"C:\dst2", Kind = OperationKind.Rename, SourceIndex = i, Detail = "renamed to avoid a conflict" }));
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = original, Root = @"C:\dst2", Kind = OperationKind.Untouched, SubjectIndex = subject, Detail = "kept" }));
                    }
                    else
                    {
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = $@"C:\dst2\file-{i}.dat", Root = @"C:\dst2", Kind = OperationKind.New, SourceIndex = i }));
                    }
                    break;

                case 1:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.SkippedByFilter,
                        SourceIndex = i,
                        Detail = "exclude *.tmp",
                    }));
                    break;

                default:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.SkippedUnchanged,
                        SourceIndex = i,
                    }));
                    string unchanged = $@"C:\dst\file-{i}.dat";
                    int unchangedSubject = destinationFiles.Count;
                    destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = unchanged, Root = @"C:\dst", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                    destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = unchanged, Root = @"C:\dst", Kind = OperationKind.SkipUnchanged, SourceIndex = i, SubjectIndex = unchangedSubject, Detail = "identical content (SHA-256)" }));
                    break;
            }
        }

        return new DryRunReport
        {
            ProfileId = Guid.NewGuid(),
            GeneratedAt = DateTimeOffset.UtcNow,
            Directories = dirs.Entries.ToList(),
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
        };
    }
}
