using Avalonia.Controls;
using FileManager.Contracts.DryRun;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real DryRunView control against a populated view model and forces a layout pass,
/// so runtime-only XAML failures (the shared TreeDataTemplate, the TreeViewItem IsExpanded style binding,
/// the VirtualizingStackPanel item panels, style selectors) surface as a failing test rather than in the
/// app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class DryRunViewSmokeTests(HeadlessSessionFixture headless)
{
    private static DryRunViewModel PopulatedViewModel()
    {
        FakeIpcGateway gateway = new();
        DryRunViewModel vm = new(gateway);
        vm.SetProfile(Guid.NewGuid(), "P");

        var files = new List<DryRunFileResult>();
        for (int i = 0; i < 200; i++)
            files.Add(new DryRunFileResult
            {
                SourcePath = $@"C:\src\dir-{i % 8}\file-{i}.dat",
                Disposition = DryRunFileDisposition.WouldProcess,
                SourceDisposition = i % 5 == 0 ? "MoveToTrash" : "KeepSource",
                Targets =
                [
                    new DryRunTargetAction
                    {
                        TargetPath = $@"D:\dst\file-{i}.dat",
                        Kind = i % 3 == 0 ? DryRunTargetKind.WouldOverwrite : DryRunTargetKind.WouldWrite,
                    },
                ],
            });
        gateway.DryRunResult = new DryRunReport(vm.ProfileId!.Value, DateTimeOffset.UnixEpoch, files);
        return vm;
    }

    private static Window ShowView(DryRunViewModel vm)
    {
        DryRunView view = new() { DataContext = vm };
        Window window = new() { Width = 1000, Height = 700, Content = view };
        window.Show();
        return window;
    }

    [Fact]
    public async Task List_view_loads_and_lays_out()
    {
        await headless.Session.Dispatch(async () =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            await vm.RunAsync(CancellationToken.None);

            Window window = ShowView(vm);

            // No exception from XAML load/layout, and the report is showing.
            Assert.True(vm.HasReport);
            Assert.NotEmpty(vm.AffectedFiles);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Source_facet_renders_and_toggling_a_source_relayouts()
    {
        await headless.Session.Dispatch(async () =>
        {
            FakeIpcGateway gateway = new();
            DryRunViewModel vm = new(gateway);
            vm.SetProfile(Guid.NewGuid(), "P");

            // Two sources so the facet is shown and its CheckBox DataTemplate realizes on layout.
            var files = new List<DryRunFileResult>();
            for (int i = 0; i < 40; i++)
                files.Add(new DryRunFileResult
                {
                    SourcePath = $@"C:\src-{i % 2}\file-{i}.dat",
                    SourceRoot = $@"C:\src-{i % 2}",
                    Disposition = DryRunFileDisposition.WouldProcess,
                    SourceDisposition = "KeepSource",
                    Targets = [new DryRunTargetAction { TargetPath = $@"D:\dst\file-{i}.dat", Kind = DryRunTargetKind.WouldWrite }],
                });
            gateway.DryRunResult = new DryRunReport(vm.ProfileId!.Value, DateTimeOffset.UnixEpoch, files);
            await vm.RunAsync(CancellationToken.None);

            Window window = ShowView(vm);

            Assert.True(vm.ShowSourceFacet);
            Assert.Equal(2, vm.SourceFacets.Count);

            int before = vm.AffectedFiles.Count;
            vm.SourceFacets[0].IsSelected = false;   // drives the facet checkbox → RebuildVisibleRows path
            Assert.True(vm.AffectedFiles.Count < before);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Tree_view_loads_and_lays_out()
    {
        await headless.Session.Dispatch(() =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            vm.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            vm.ShowTree = true;               // build the forest before showing so the TreeView realizes on layout

            Window window = ShowView(vm);     // no throw ⇒ TreeDataTemplate + VSP styles + IsExpanded binding are valid

            Assert.NotEmpty(vm.AffectedTree);
            DryRunTreeNode dir = vm.AffectedTree.First(n => n.IsDirectory && n.HasChildren);

            // Expansion is the control's job now; flipping the model flag must not throw as the view reacts.
            dir.IsExpanded = !dir.IsExpanded;
            Assert.NotNull(window.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Skip_rows_render_in_the_merged_tree()
    {
        await headless.Session.Dispatch(() =>
        {
            FakeIpcGateway gateway = new();
            DryRunViewModel vm = new(gateway);
            vm.SetProfile(Guid.NewGuid(), "P");

            // A report with filter-skip + unchanged rows so the merged tree carries every skip status'
            // rollup chips (exercises the chipMuted/chipAccent node chips in the shared template).
            var files = new List<DryRunFileResult>();
            for (int i = 0; i < 30; i++)
                files.Add(new DryRunFileResult
                {
                    SourcePath = $@"C:\src\dir-{i % 4}\junk-{i}.tmp",
                    Disposition = DryRunFileDisposition.WouldSkipFilter,
                    DecidingFilter = "exclude *.tmp",
                });
            for (int i = 0; i < 30; i++)
                files.Add(new DryRunFileResult
                {
                    SourcePath = $@"C:\src\dir-{i % 4}\same-{i}.dat",
                    Disposition = DryRunFileDisposition.WouldSkipUnchanged,
                    Targets = [new DryRunTargetAction { TargetPath = $@"D:\dst\same-{i}.dat", Kind = DryRunTargetKind.WouldSkipUnchanged }],
                });
            gateway.DryRunResult = new DryRunReport(vm.ProfileId!.Value, DateTimeOffset.UnixEpoch, files);
            vm.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

            vm.ShowTree = true;

            Window window = ShowView(vm);     // no throw ⇒ the shared tree template renders the skip-status chips

            DryRunTreeNode root = Assert.Single(vm.AffectedTree);
            Assert.True(root.HasFiltered);
            Assert.True(root.HasUnchanged);
        }, CancellationToken.None);
    }
}
