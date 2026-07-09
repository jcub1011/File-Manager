using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class DryRunViewModelTests
{
    private static (DryRunViewModel ViewModel, FakeIpcGateway Gateway) NewViewModel()
    {
        FakeIpcGateway gateway = new();
        DryRunViewModel viewModel = new(gateway, new FakeFolderPicker());
        viewModel.SetProfile(Guid.NewGuid(), "P");
        return (viewModel, gateway);
    }

    private static DryRunReport SampleReport(Guid profileId) => new(profileId, DateTimeOffset.UnixEpoch,
    [
        new DryRunFileResult
        {
            SourcePath = @"C:\s\fresh.txt",
            Disposition = DryRunFileDisposition.WouldProcess,
            SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\fresh.txt", Kind = DryRunTargetKind.WouldWrite }],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\clobber.txt",
            Disposition = DryRunFileDisposition.WouldProcess,
            SourceDisposition = "MoveToTrash",
            Targets =
            [
                new DryRunTargetAction { TargetPath = @"C:\t\clobber.txt", Kind = DryRunTargetKind.WouldOverwrite },
                new DryRunTargetAction { TargetPath = @"C:\t2\clobber.txt", Kind = DryRunTargetKind.WouldRenameTo, Detail = @"C:\t2\clobber (1).txt" },
            ],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\junk.tmp",
            Disposition = DryRunFileDisposition.WouldSkipFilter,
            DecidingFilter = "Exclude pattern glob *.tmp",
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\same.txt",
            Disposition = DryRunFileDisposition.WouldSkipUnchanged,
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\same.txt", Kind = DryRunTargetKind.WouldSkipUnchanged }],
        },
    ]);

    [Fact]
    public async Task Summary_counts_reflect_the_report()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.HasReport);
        Assert.Equal(4, viewModel.TotalFiles);
        Assert.Equal(2, viewModel.ProcessCount);
        Assert.Equal(1, viewModel.FilterSkipCount);
        Assert.Equal(1, viewModel.UnchangedSkipCount);
        Assert.Equal(1, viewModel.OverwriteCount);
        Assert.Equal(1, viewModel.RenameCount);
        Assert.Equal(1, viewModel.DisposalCount);   // MoveToTrash; KeepSource is not a disposal
        Assert.True(viewModel.HasDestructiveActions);
    }

    [Fact]
    public async Task Groups_split_by_disposition()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);
        // The sample report has destructive actions, so it auto-focuses; drop that to see all buckets.
        viewModel.DestructiveOnly = false;

        Assert.Equal(2, viewModel.ProcessFiles.Count);
        Assert.Single(viewModel.FilterSkips);
        Assert.Single(viewModel.UnchangedSkips);
        Assert.Contains("*.tmp", viewModel.FilterSkips[0].DecidingFilter);
    }

    [Fact]
    public async Task Report_with_destructive_actions_auto_focuses_the_destructive_subset()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.DestructiveOnly);
        DryRunFileRow row = Assert.Single(viewModel.ProcessFiles);
        Assert.EndsWith("clobber.txt", row.SourcePath);
        Assert.Empty(viewModel.FilterSkips);
        Assert.Empty(viewModel.UnchangedSkips);
    }

    [Fact]
    public async Task Benign_report_shows_everything_by_default()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new DryRunReport(viewModel.ProfileId!.Value, DateTimeOffset.UnixEpoch,
        [
            new DryRunFileResult
            {
                SourcePath = @"C:\s\a.txt",
                Disposition = DryRunFileDisposition.WouldProcess,
                SourceDisposition = "KeepSource",
                Targets = [new DryRunTargetAction { TargetPath = @"C:\t\a.txt", Kind = DryRunTargetKind.WouldWrite }],
            },
            new DryRunFileResult
            {
                SourcePath = @"C:\s\b.tmp",
                Disposition = DryRunFileDisposition.WouldSkipFilter,
                DecidingFilter = "exclude *.tmp",
            },
        ]);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasDestructiveActions);
        Assert.False(viewModel.DestructiveOnly);
        Assert.Single(viewModel.ProcessFiles);
        Assert.Single(viewModel.FilterSkips);
    }

    [Fact]
    public async Task Destructive_only_filters_to_overwrites_and_disposals()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.DestructiveOnly = true;

        DryRunFileRow row = Assert.Single(viewModel.ProcessFiles);
        Assert.EndsWith("clobber.txt", row.SourcePath);
        Assert.Empty(viewModel.FilterSkips);
        Assert.Empty(viewModel.UnchangedSkips);

        viewModel.DestructiveOnly = false;
        Assert.Equal(2, viewModel.ProcessFiles.Count);
    }

    [Fact]
    public async Task Search_filters_the_process_list_by_path_substring()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.DestructiveOnly = false;   // see all process rows

        viewModel.SearchText = "fresh";

        DryRunFileRow row = Assert.Single(viewModel.ProcessFiles);
        Assert.EndsWith("fresh.txt", row.SourcePath);

        viewModel.SearchText = "";
        Assert.Equal(2, viewModel.ProcessFiles.Count);
    }

    [Fact]
    public async Task Search_matches_target_paths_too()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.DestructiveOnly = false;

        viewModel.SearchText = @"t2\clobber";   // only clobber.txt has a t2 target

        DryRunFileRow row = Assert.Single(viewModel.ProcessFiles);
        Assert.EndsWith("clobber.txt", row.SourcePath);
    }

    [Fact]
    public async Task Large_process_list_binds_every_row_without_truncation()
    {
        var (viewModel, gateway) = NewViewModel();
        var files = new List<DryRunFileResult>();
        for (int i = 0; i < 5_000; i++)
            files.Add(new DryRunFileResult
            {
                SourcePath = $@"C:\s\file-{i}.txt",
                Disposition = DryRunFileDisposition.WouldProcess,
                SourceDisposition = "KeepSource",
                Targets = [new DryRunTargetAction { TargetPath = $@"C:\t\file-{i}.txt", Kind = DryRunTargetKind.WouldWrite }],
            });
        gateway.DryRunResult = new DryRunReport(viewModel.ProfileId!.Value, DateTimeOffset.UnixEpoch, files);

        await viewModel.RunAsync(CancellationToken.None);

        // The list virtualizes, so all matches are bound — nothing is truncated for display.
        Assert.False(viewModel.DestructiveOnly);
        Assert.Equal(5_000, viewModel.ProcessFiles.Count);
    }

    [Fact]
    public async Task Tree_view_rolls_files_into_directory_nodes_with_aggregated_counts()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.DestructiveOnly = false;   // include both process rows (fresh.txt + clobber.txt)

        viewModel.ShowTree = true;

        // Both process files live under C:\s — the root node aggregates the whole subtree.
        DryRunTreeNode root = Assert.Single(viewModel.TreeRows, n => n.Name == "C:");
        Assert.True(root.IsDirectory);
        Assert.Equal(2, root.FileCount);       // fresh.txt + clobber.txt
        Assert.Equal(1, root.OverwriteCount);  // clobber.txt's overwrite target
        Assert.Equal(1, root.RenameCount);     // clobber.txt's rename target
        Assert.Equal(1, root.DisposalCount);   // clobber.txt's MoveToTrash
    }

    [Fact]
    public async Task Toggling_a_tree_node_expands_and_collapses_its_children()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.DestructiveOnly = false;
        viewModel.ShowTree = true;

        // Only the top-level root expands by default, so the "s" directory is visible but its leaves
        // are hidden until it is expanded.
        DryRunTreeNode sDir = Assert.Single(viewModel.TreeRows, n => n.Name == "s");
        Assert.DoesNotContain(viewModel.TreeRows, n => n.Name.EndsWith(".txt"));

        viewModel.ToggleNodeCommand.Execute(sDir);
        Assert.Contains(viewModel.TreeRows, n => n.Name.EndsWith(".txt"));

        viewModel.ToggleNodeCommand.Execute(sDir);
        Assert.DoesNotContain(viewModel.TreeRows, n => n.Name.EndsWith(".txt"));
    }

    [Fact]
    public async Task Gateway_error_surfaces_as_a_banner()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new IpcError("DRY_RUN_FAILED", "scan failed: boom");

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasReport);
        Assert.Contains("boom", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Truncated_report_surfaces_a_notice_and_a_full_report_clears_it()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with { Truncated = true };

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.WasTruncated);
        Assert.Contains("4", viewModel.TruncationNotice);
        Assert.Contains("truncated", viewModel.TruncationNotice, StringComparison.OrdinalIgnoreCase);

        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.WasTruncated);
        Assert.Equal("", viewModel.TruncationNotice);
    }

    [Fact]
    public async Task Transport_errors_point_at_the_service_log()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new IpcError("IPC_TRANSPORT", "connection closed");

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasReport);
        Assert.Contains("connection closed", viewModel.ErrorMessage);
        Assert.Contains(@"FileManager\logs", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Unexpected_gateway_exception_surfaces_as_a_banner_not_a_fault()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunException = new InvalidOperationException("wire format drifted");

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasReport);
        Assert.Contains("wire format drifted", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Scope_path_is_forwarded_trimmed_or_null()
    {
        var (viewModel, gateway) = NewViewModel();
        viewModel.ScopePath = "   ";
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.ScopePath = @" C:\s\sub ";
        await viewModel.RunAsync(CancellationToken.None);

        Assert.Null(gateway.DryRunCalls[0].Scope);
        Assert.Equal(@"C:\s\sub", gateway.DryRunCalls[1].Scope);
    }

    [Fact]
    public async Task Cancellation_resets_to_a_calm_state()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunGate = new TaskCompletionSource();
        using CancellationTokenSource cts = new();

        Task running = viewModel.RunAsync(cts.Token);
        cts.Cancel();
        await running;

        Assert.False(viewModel.HasReport);
        Assert.Contains("cancelled", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
