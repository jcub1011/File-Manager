using Avalonia.Controls;
using Avalonia.VisualTree;
using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real DryRunView control against a populated view model and forces a layout pass,
/// so runtime-only XAML failures (the shared TreeDataTemplate + data-driven pills, the TreeViewItem
/// IsExpanded style binding, the VirtualizingStackPanel item panels, the tabbed list/tree templates,
/// facet checkbox templates) surface as a failing test rather than in the app. Each test closes its
/// window so the headless render loop stops and the shared session tears down cleanly.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class DryRunViewSmokeTests(HeadlessSessionFixture headless)
{
    private static DryRunViewModel PopulatedViewModel()
    {
        FakeIpcGateway gateway = new();
        // Zero debounce: no pending Task.Delay is scheduled on the headless dispatcher.
        DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
        vm.SetProfile(Guid.NewGuid(), "P");

        var files = new List<DryRunFileResult>();
        for (int i = 0; i < 200; i++)
            files.Add(new DryRunFileResult
            {
                SourcePath = $@"C:\src\dir-{i % 8}\file-{i}.dat",
                SourceRoot = @"C:\src",
                Disposition = DryRunFileDisposition.WouldProcess,
                SourceDisposition = i % 5 == 0 ? "MoveToTrash" : "KeepSource",
                Targets =
                [
                    new DryRunTargetAction
                    {
                        TargetPath = $@"D:\dst\file-{i}.dat",
                        TargetRoot = @"D:\dst",
                        Kind = i % 3 == 0 ? DryRunTargetKind.WouldOverwrite : DryRunTargetKind.WouldWrite,
                    },
                ],
            });
        gateway.DryRunResult = new DryRunReport(vm.ProfileId!.Value, DateTimeOffset.UnixEpoch, files,
            Destinations:
            [
                new DryRunDestinationEntry { TargetPath = @"D:\dst\preexisting.dat", TargetRoot = @"D:\dst", Disposition = DryRunDestinationDisposition.Untouched },
                new DryRunDestinationEntry { TargetPath = @"D:\dst\orphan.dat", TargetRoot = @"D:\dst", Disposition = DryRunDestinationDisposition.Deleted },
            ]);
        return vm;
    }

    private static (Window Window, DryRunView View) ShowView(DryRunViewModel vm)
    {
        DryRunView view = new() { DataContext = vm };
        Window window = new() { Width = 1000, Height = 700, Content = view };
        window.Show();
        return (window, view);
    }

    private static void SelectTab(DryRunView view, int index) =>
        view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = index;

    [Fact]
    public async Task Sources_list_view_loads_and_lays_out()
    {
        await headless.Session.Dispatch(async () =>
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
        await headless.Session.Dispatch(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            await vm.RunAsync(CancellationToken.None);

            var (window, view) = ShowView(vm);
            try
            {
                SelectTab(view, 1);   // realize the Destinations tab's templates
                Assert.NotEmpty(vm.Destinations.VisibleRows);
                Assert.Contains(vm.Destinations.VisibleRows, r => r.IsDeleted);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Source_facet_renders_and_toggling_a_source_relayouts()
    {
        await headless.Session.Dispatch(async () =>
        {
            FakeIpcGateway gateway = new();
            DryRunViewModel vm = new(gateway, searchDebounce: TimeSpan.Zero);
            vm.SetProfile(Guid.NewGuid(), "P");

            var files = new List<DryRunFileResult>();
            for (int i = 0; i < 40; i++)
                files.Add(new DryRunFileResult
                {
                    SourcePath = $@"C:\src-{i % 2}\file-{i}.dat",
                    SourceRoot = $@"C:\src-{i % 2}",
                    Disposition = DryRunFileDisposition.WouldProcess,
                    SourceDisposition = "KeepSource",
                    Targets = [new DryRunTargetAction { TargetPath = $@"D:\dst\file-{i}.dat", TargetRoot = @"D:\dst", Kind = DryRunTargetKind.WouldWrite }],
                });
            gateway.DryRunResult = new DryRunReport(vm.ProfileId!.Value, DateTimeOffset.UnixEpoch, files);
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
        await headless.Session.Dispatch(() =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            vm.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
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
        await headless.Session.Dispatch(() =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            vm.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            vm.Destinations.ShowTree = true;

            var (window, view) = ShowView(vm);
            try
            {
                SelectTab(view, 1);
                Assert.NotEmpty(vm.Destinations.Tree);
                DryRunTreeNode root = vm.Destinations.Tree.First();
                Assert.NotEmpty(root.Pills);          // rolled-up new/overwritten/untouched/deleted pills render
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
