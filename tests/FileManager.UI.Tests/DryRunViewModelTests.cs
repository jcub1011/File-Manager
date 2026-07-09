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
        // Zero debounce keeps search-driven rebuilds synchronous so tests can assert immediately.
        DryRunViewModel viewModel = new(gateway, searchDebounce: TimeSpan.Zero);
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

    // Two sources (C:\a with one process + one filter-skip, C:\b with two process), benign so nothing
    // auto-focuses and every row is visible for the facet assertions.
    private static DryRunReport MultiSourceReport(Guid profileId) => new(profileId, DateTimeOffset.UnixEpoch,
    [
        new DryRunFileResult
        {
            SourcePath = @"C:\a\one.txt", SourceRoot = @"C:\a",
            Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\one.txt", Kind = DryRunTargetKind.WouldWrite }],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\a\junk.tmp", SourceRoot = @"C:\a",
            Disposition = DryRunFileDisposition.WouldSkipFilter, DecidingFilter = "exclude *.tmp",
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\b\three.txt", SourceRoot = @"C:\b",
            Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\three.txt", Kind = DryRunTargetKind.WouldWrite }],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\b\four.txt", SourceRoot = @"C:\b",
            Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\four.txt", Kind = DryRunTargetKind.WouldWrite }],
        },
    ]);

    [Fact]
    public async Task Source_facet_lists_each_distinct_source_with_its_process_count()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = MultiSourceReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.ShowSourceFacet);
        Assert.Equal(2, viewModel.SourceFacets.Count);
        Assert.All(viewModel.SourceFacets, f => Assert.True(f.IsSelected));
        Assert.Equal(1, viewModel.SourceFacets.Single(f => f.Root == @"C:\a").Count);
        Assert.Equal(2, viewModel.SourceFacets.Single(f => f.Root == @"C:\b").Count);
    }

    [Fact]
    public async Task Toggling_a_source_off_filters_the_lists_but_leaves_the_banner_intact()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = MultiSourceReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // Baseline: benign report shows everything from both sources.
        Assert.False(viewModel.DestructiveOnly);
        Assert.Equal(3, viewModel.ProcessFiles.Count);
        Assert.Single(viewModel.FilterSkips);

        viewModel.SourceFacets.Single(f => f.Root == @"C:\a").IsSelected = false;

        // Only C:\b's rows remain visible; C:\a's process row and filter-skip drop out.
        Assert.Equal(2, viewModel.ProcessFiles.Count);
        Assert.All(viewModel.ProcessFiles, r => Assert.Equal(@"C:\b", r.SourceRoot));
        Assert.Empty(viewModel.FilterSkips);

        // The blast-radius banner always reflects the complete run — a view filter never changes it.
        Assert.Equal(4, viewModel.TotalFiles);
        Assert.Equal(3, viewModel.ProcessCount);
        Assert.Equal(1, viewModel.FilterSkipCount);
    }

    [Fact]
    public async Task Single_source_report_shows_no_facet()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new DryRunReport(viewModel.ProfileId!.Value, DateTimeOffset.UnixEpoch,
        [
            new DryRunFileResult
            {
                SourcePath = @"C:\only\a.txt", SourceRoot = @"C:\only",
                Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
                Targets = [new DryRunTargetAction { TargetPath = @"C:\t\a.txt", Kind = DryRunTargetKind.WouldWrite }],
            },
        ]);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.ShowSourceFacet);
        Assert.Empty(viewModel.SourceFacets);
    }

    [Fact]
    public async Task Dry_run_forwards_the_profile_id()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.Equal(viewModel.ProfileId!.Value, Assert.Single(gateway.DryRunCalls));
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
