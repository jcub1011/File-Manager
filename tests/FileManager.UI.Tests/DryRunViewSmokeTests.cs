using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real DryRunView control against a populated view model and forces a layout pass,
/// so runtime-only XAML failures (the TreeDataGrid columns + shared cell templates, the data-driven
/// pills, the expander column's Children/HasChildren/IsExpanded bindings, the tabbed list/tree
/// templates, facet checkbox templates) surface as a failing test rather than in the app. Each test
/// closes its window so the headless render loop stops and the shared session tears down cleanly.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class DryRunViewSmokeTests(HeadlessSessionFixture headless)
{
    private DryRunViewModel PopulatedViewModel()
    {
        FakeIpcGateway gateway = new();
        // Zero debounce: no pending Task.Delay is scheduled on the headless dispatcher.
        DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
        vm.SetProfile(Guid.NewGuid(), "P");

        var sourceFiles = new List<DryRunFile>();
        var sourceOps = new List<DryRunOperation>();
        var destinationFiles = new List<DryRunFile>();
        var destinationOps = new List<DryRunOperation>();
        for (int i = 0; i < 200; i++)
        {
            string source = $@"C:\src\dir-{i % 8}\file-{i}.dat";
            string target = $@"D:\dst\file-{i}.dat";
            sourceFiles.Add(Pf(source, @"C:\src"));
            sourceOps.Add(SrcOp(i, source, @"C:\src", OperationKind.Processed,
                i % 5 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource));
            if (i % 3 == 0)
            {
                int subject = destinationFiles.Count;
                destinationFiles.Add(Pf(target, @"D:\dst"));
                destinationOps.Add(DstOp(OperationKind.Overwrite, target, @"D:\dst", sourceIndex: i, subjectIndex: subject));
            }
            else
            {
                destinationOps.Add(DstOp(OperationKind.New, target, @"D:\dst", sourceIndex: i));
            }
        }
        // Pre-existing untouched + a Mirror orphan (Deleted), destination-only (SourceIndex == -1).
        int keepSubject = destinationFiles.Count;
        destinationFiles.Add(Pf(@"D:\dst\preexisting.dat", @"D:\dst"));
        destinationOps.Add(DstOp(OperationKind.Untouched, @"D:\dst\preexisting.dat", @"D:\dst", subjectIndex: keepSubject));
        int orphanSubject = destinationFiles.Count;
        destinationFiles.Add(Pf(@"D:\dst\orphan.dat", @"D:\dst"));
        destinationOps.Add(DstOp(OperationKind.Deleted, @"D:\dst\orphan.dat", @"D:\dst", subjectIndex: orphanSubject));

        gateway.DryRunResult = Report(vm.ProfileId!.Value, sourceFiles, sourceOps, destinationFiles, destinationOps);
        return vm;
    }

    // New-model fixture helpers (see DryRunViewModelTests for the model shape). Paths run through a
    // per-test directory table (xunit news the class up per test, so one builder per report).
    private readonly DryRunDirectoryTableBuilder _dirs = new();

    private DryRunFile Pf(string path, string root) =>
        _dirs.Convert(new PhysicalFile { Path = path, Root = root, Length = 0, LastWritten = DateTimeOffset.UnixEpoch, IsReparsePoint = false });

    private DryRunOperation SrcOp(
        int index, string path, string root, OperationKind kind, OnSuccessAction? disposition = null, string? detail = null) =>
        _dirs.Convert(new VirtualFileOperation { Path = path, Root = root, Kind = kind, SourceIndex = index, SubjectIndex = -1, SourceDisposition = disposition, Detail = detail });

    private DryRunOperation DstOp(
        OperationKind kind, string path, string root, int sourceIndex = -1, int subjectIndex = -1, string? detail = null) =>
        _dirs.Convert(new VirtualFileOperation { Path = path, Root = root, Kind = kind, SourceIndex = sourceIndex, SubjectIndex = subjectIndex, Detail = detail });

    private DryRunReport Report(
        Guid profileId,
        IReadOnlyList<DryRunFile> sourceFiles,
        IReadOnlyList<DryRunOperation> sourceOps,
        IReadOnlyList<DryRunFile> destinationFiles,
        IReadOnlyList<DryRunOperation> destinationOps) =>
        new()
        {
            ProfileId = profileId,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = _dirs.Entries.ToList(),
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
        };

    private static (Window Window, DryRunView View) ShowView(DryRunViewModel vm)
    {
        DryRunView view = new() { DataContext = vm };
        Window window = new() { Width = 1000, Height = 700, Content = view };
        window.Show();
        return (window, view);
    }

    [Fact]
    public async Task Sources_list_view_loads_and_lays_out()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            await vm.RunAsync(CancellationToken.None);

            var (window, _) = ShowView(vm);
            try
            {
                Assert.True(vm.HasReport);
                Assert.NotEmpty(vm.Sources.VisibleRows);
                Assert.NotNull(window.Content);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Destinations_tab_loads_and_lays_out()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            await vm.RunAsync(CancellationToken.None);

            var (window, _) = ShowView(vm);   // both panels realize at once — no tab to select
            try
            {
                Assert.NotEmpty(vm.Destinations.VisibleRows);
                Assert.Contains(vm.Destinations.VisibleRows, r => r.Destinations.Any(d => d.IsDeleted));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Destinations_tab_lays_out_a_replicated_files_wrapping_chip_list()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new();
            DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
            vm.SetProfile(Guid.NewGuid(), "P");
            // One source fanned out to several nested targets → one grouped row of wrapping chips.
            var srcOps = new List<DryRunOperation>
                { SrcOp(0, @"C:\src\reports\annual-summary.docx", @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource) };
            var dstOps = new List<DryRunOperation>
            {
                DstOp(OperationKind.New, @"D:\backup\2026\reports\annual-summary.docx", @"D:\backup", sourceIndex: 0),
                DstOp(OperationKind.Overwrite, @"E:\archive\deep\nested\path\reports\annual-summary.docx", @"E:\archive", sourceIndex: 0),
                DstOp(OperationKind.New, @"F:\mirror\reports\annual-summary.docx", @"F:\mirror", sourceIndex: 0),
            };
            gateway.DryRunResult = Report(vm.ProfileId!.Value,
                [Pf(@"C:\src\reports\annual-summary.docx", @"C:\src")], srcOps, [], dstOps);
            await vm.RunAsync(CancellationToken.None);

            var (window, _) = ShowView(vm);
            try
            {
                DryRunDestinationRow row = Assert.Single(vm.Destinations.VisibleRows);
                Assert.True(row.HasSource);
                Assert.Equal(3, row.Destinations.Count);
                Assert.NotNull(window.Content);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Source_facet_renders_and_toggling_a_source_relayouts()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new();
            DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
            vm.SetProfile(Guid.NewGuid(), "P");

            var sourceFiles = new List<DryRunFile>();
            var sourceOps = new List<DryRunOperation>();
            var destinationOps = new List<DryRunOperation>();
            for (int i = 0; i < 40; i++)
            {
                string source = $@"C:\src-{i % 2}\file-{i}.dat";
                string root = $@"C:\src-{i % 2}";
                sourceFiles.Add(Pf(source, root));
                sourceOps.Add(SrcOp(i, source, root, OperationKind.Processed, OnSuccessAction.KeepSource));
                destinationOps.Add(DstOp(OperationKind.New, $@"D:\dst\file-{i}.dat", @"D:\dst", sourceIndex: i));
            }
            gateway.DryRunResult = Report(vm.ProfileId!.Value, sourceFiles, sourceOps, [], destinationOps);
            await vm.RunAsync(CancellationToken.None);

            var (window, _) = ShowView(vm);
            try
            {
                Assert.True(vm.Sources.ShowSourceFacet);
                Assert.Equal(2, vm.Sources.SourceFacets.Count);

                int before = vm.Sources.VisibleRows.Count;
                vm.Sources.SourceFacets[0].IsSelected = false;   // drives the facet checkbox → rebuild path
                Assert.True(vm.Sources.VisibleRows.Count < before);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Sources_tree_view_loads_and_lays_out()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            // RunAsync hops to the thread pool for report preparation, so blocking the dispatcher
            // thread with GetResult() here would deadlock — await it instead.
            await vm.RunAsync(CancellationToken.None);
            vm.Sources.ShowTree = true;               // build the forest before showing so the TreeView realizes

            var (window, _) = ShowView(vm);           // no throw ⇒ TreeDataTemplate + pills + VSP + IsExpanded binding valid
            try
            {
                Assert.NotEmpty(vm.Sources.Tree);
                DryRunTreeNode dir = vm.Sources.Tree.First(n => n.IsDirectory && n.HasChildren);
                dir.IsExpanded = !dir.IsExpanded;     // expansion is the control's job; flipping must not throw
                Assert.NotNull(window.Content);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Destinations_tree_view_loads_and_lays_out()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            // RunAsync hops to the thread pool for report preparation, so blocking the dispatcher
            // thread with GetResult() here would deadlock — await it instead.
            await vm.RunAsync(CancellationToken.None);
            vm.Destinations.ShowTree = true;

            var (window, _) = ShowView(vm);
            try
            {
                Assert.NotEmpty(vm.Destinations.Tree);
                DryRunTreeNode node = vm.Destinations.Tree.First();
                Assert.NotEmpty(node.Pills);          // rolled-up new/overwritten/untouched/deleted pills render
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Panels_lay_out_side_by_side_in_a_narrow_window()
    {
        // A narrow window exercises the toolbar/search-box shrink and the min-width columns without clipping.
        await headless.Session.DispatchAsync(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            await vm.RunAsync(CancellationToken.None);

            DryRunView view = new() { DataContext = vm };
            Window window = new() { Width = 760, Height = 700, Content = view };
            window.Show();
            try
            {
                Assert.NotEmpty(vm.Sources.VisibleRows);
                Assert.NotEmpty(vm.Destinations.VisibleRows);
                Assert.NotNull(window.Content);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Storage_panel_and_bars_lay_out_with_a_projection()
    {
        // Exercises the StorageBar render (capacity-known bar + margin line), the per-folder Expander,
        // and the capacity-unknown text branch — runtime-only paths not reachable from the VM tests.
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new();
            DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
            vm.SetProfile(Guid.NewGuid(), "P");
            DryRunReport report = Report(vm.ProfileId!.Value,
                [Pf(@"C:\s\a.dat", @"C:\s")],
                [SrcOp(0, @"C:\s\a.dat", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource)],
                [],
                [DstOp(OperationKind.New, @"D:\d\a.dat", @"D:\d", sourceIndex: 0)]);
            var space = new SpaceProjection
            {
                TotalBytesWritten = 5000,
                TotalNetChangeBytes = 4000,
                SafetyMarginBytes = 100,
                Volumes =
                [
                    new VolumeSpaceEstimate
                    {
                        VolumeRoot = "D:", CapacityKnown = true, TotalCapacityBytes = 1000, UsedNowBytes = 500, FreeNowBytes = 500,
                        ClusterBytes = 1, BytesWrittenBytes = 5000, NetChangeBytes = 400, SettledUsedBytes = 900,
                        RealisticPeakUsedBytes = 950, SafeCeilingUsedBytes = 980,
                        Folders =
                        [
                            new FolderSpaceBreakdown { Root = @"D:\d", BytesWrittenBytes = 4000, NetChangeBytes = 300, FileCount = 1 },
                            new FolderSpaceBreakdown { Root = @"D:\e", BytesWrittenBytes = 1000, NetChangeBytes = 100, FileCount = 1 },
                        ],
                    },
                    new VolumeSpaceEstimate { VolumeRoot = "Z:", CapacityKnown = false, BytesWrittenBytes = 10, NetChangeBytes = 10 },
                ],
            };
            gateway.DryRunResult = report with { Space = space };
            await vm.RunAsync(CancellationToken.None);

            var (window, _) = ShowView(vm);
            try
            {
                Assert.NotNull(vm.Space);
                Assert.Equal(2, vm.Space!.Volumes.Count);
                Assert.NotNull(window.Content);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Resizing_relayouts_path_rows_without_reentrancy()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            await vm.RunAsync(CancellationToken.None);

            var (window, _) = ShowView(vm);
            try
            {
                window.Width = 640;             // PathText must re-truncate on a second layout pass
                Dispatcher.UIThread.RunJobs();  // completing (no hang / stack overflow) ⇒ no measure/arrange loop
                Assert.NotNull(window.Content);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
