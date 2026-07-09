using Avalonia.Controls;
using FileManager.Contracts.DryRun;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real DryRunView control against a populated view model and forces a layout pass,
/// so runtime-only XAML failures (the $parent[ListBox] command binding, the Thickness Indent binding,
/// style selectors, the virtualizing ListBox panels) surface as a failing test rather than in the app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class DryRunViewSmokeTests(HeadlessSessionFixture headless)
{
    private static DryRunViewModel PopulatedViewModel()
    {
        FakeIpcGateway gateway = new();
        DryRunViewModel vm = new(gateway, new FakeFolderPicker());
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
            Assert.NotEmpty(vm.ProcessFiles);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Tree_view_loads_and_toggling_a_node_relayouts()
    {
        await headless.Session.Dispatch(() =>
        {
            DryRunViewModel vm = PopulatedViewModel();
            vm.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            vm.DestructiveOnly = false;

            Window window = ShowView(vm);

            vm.ShowTree = true;                       // realizes the tree ListBox + Indent/command bindings
            Assert.NotEmpty(vm.TreeRows);

            DryRunTreeNode dir = vm.TreeRows.First(n => n.IsDirectory && n.HasChildren);
            int before = vm.TreeRows.Count;
            vm.ToggleNodeCommand.Execute(dir);        // drives the $parent[ListBox] command path
            Assert.NotEqual(before, vm.TreeRows.Count);
        }, CancellationToken.None);
    }
}
