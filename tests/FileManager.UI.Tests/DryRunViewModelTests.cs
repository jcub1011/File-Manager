using System.Linq;
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
    public async Task Affected_files_merge_every_disposition_in_path_order()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        // One merged list carries every row, each tagged with its own disposition.
        Assert.Equal(4, viewModel.AffectedFiles.Count);
        Assert.Equal(2, viewModel.AffectedFiles.Count(r => r.IsProcessed));
        Assert.Single(viewModel.AffectedFiles, r => r.IsFilterSkipped);
        Assert.Single(viewModel.AffectedFiles, r => r.IsUnchangedSkipped);
        Assert.Contains("*.tmp", viewModel.AffectedFiles.Single(r => r.IsFilterSkipped).DecidingFilter);

        // Sorted by path, interleaving dispositions rather than grouping by them.
        var paths = viewModel.AffectedFiles.Select(r => r.SourcePath).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), paths);
    }

    [Fact]
    public async Task Row_status_helpers_reflect_disposition()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        DryRunFileRow processed = viewModel.AffectedFiles.First(r => r.IsProcessed);
        Assert.Equal("Processed", processed.StatusText);
        Assert.Equal(1.0, processed.ContentOpacity);

        DryRunFileRow filtered = viewModel.AffectedFiles.Single(r => r.IsFilterSkipped);
        Assert.Equal("Skipped", filtered.StatusText);
        Assert.Equal(0.55, filtered.ContentOpacity);   // filter-skipped rows dim their content

        DryRunFileRow unchanged = viewModel.AffectedFiles.Single(r => r.IsUnchangedSkipped);
        Assert.Equal("Unchanged", unchanged.StatusText);
        Assert.Equal(1.0, unchanged.ContentOpacity);
    }

    [Fact]
    public async Task Report_with_destructive_actions_opens_unfiltered()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        // The old auto-focus behavior is gone: a risky report still opens with nothing hidden.
        Assert.True(viewModel.HasDestructiveActions);
        Assert.False(viewModel.ShowDestructive);
        Assert.True(viewModel.ShowProcessed);
        Assert.True(viewModel.ShowFiltered);
        Assert.True(viewModel.ShowUnchanged);
        Assert.Equal(4, viewModel.AffectedFiles.Count);
    }

    [Fact]
    public async Task Filter_pills_narrow_the_visible_rows()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.Equal(4, viewModel.AffectedFiles.Count);

        viewModel.ShowFiltered = false;
        Assert.Equal(3, viewModel.AffectedFiles.Count);
        Assert.DoesNotContain(viewModel.AffectedFiles, r => r.IsFilterSkipped);

        viewModel.ShowUnchanged = false;
        Assert.Equal(2, viewModel.AffectedFiles.Count);
        Assert.All(viewModel.AffectedFiles, r => Assert.True(r.IsProcessed));

        viewModel.ShowProcessed = false;
        Assert.Empty(viewModel.AffectedFiles);

        // Full-run counts on the pills/banner are untouched by the view filters.
        Assert.Equal(2, viewModel.ProcessCount);
        Assert.Equal(1, viewModel.FilterSkipCount);
        Assert.Equal(1, viewModel.UnchangedSkipCount);
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
        Assert.False(viewModel.ShowDestructive);
        Assert.Equal(2, viewModel.AffectedFiles.Count);
        Assert.Single(viewModel.AffectedFiles, r => r.IsProcessed);
        Assert.Single(viewModel.AffectedFiles, r => r.IsFilterSkipped);
    }

    [Fact]
    public async Task Destructive_pill_shows_only_risky_processed_rows_alongside_skips()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.ShowDestructive = true;

        // Selecting Destructive deselects "Would process" (they are mutually exclusive views of the
        // processed rows); only the risky processed row (clobber.txt) shows, plus the untouched skips.
        Assert.False(viewModel.ShowProcessed);
        DryRunFileRow processed = Assert.Single(viewModel.AffectedFiles, r => r.IsProcessed);
        Assert.EndsWith("clobber.txt", processed.SourcePath);
        Assert.Single(viewModel.AffectedFiles, r => r.IsFilterSkipped);
        Assert.Single(viewModel.AffectedFiles, r => r.IsUnchangedSkipped);

        // Re-selecting "Would process" deselects Destructive and restores the full processed set.
        viewModel.ShowProcessed = true;
        Assert.False(viewModel.ShowDestructive);
        Assert.Equal(2, viewModel.AffectedFiles.Count(r => r.IsProcessed));
    }

    [Fact]
    public async Task Destructive_pill_with_no_risky_rows_hides_all_processed_rows()
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

        Assert.Equal(0, viewModel.DestructiveCount);

        viewModel.ShowDestructive = true;

        // No processed row is destructive, so none show; the filter-skip row (its own pill still on)
        // remains visible.
        Assert.DoesNotContain(viewModel.AffectedFiles, r => r.IsProcessed);
        Assert.Single(viewModel.AffectedFiles, r => r.IsFilterSkipped);
    }

    [Fact]
    public async Task Search_filters_the_list_by_path_substring()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.SearchText = "fresh";

        DryRunFileRow row = Assert.Single(viewModel.AffectedFiles);
        Assert.EndsWith("fresh.txt", row.SourcePath);

        viewModel.SearchText = "";
        Assert.Equal(4, viewModel.AffectedFiles.Count);
    }

    [Fact]
    public async Task Search_matches_target_paths_too()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.SearchText = @"t2\clobber";   // only clobber.txt has a t2 target

        DryRunFileRow row = Assert.Single(viewModel.AffectedFiles);
        Assert.EndsWith("clobber.txt", row.SourcePath);
    }

    [Fact]
    public async Task Large_list_binds_every_row_without_truncation()
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
        Assert.False(viewModel.ShowDestructive);
        Assert.Equal(5_000, viewModel.AffectedFiles.Count);
    }

    [Fact]
    public async Task Tree_view_rolls_files_into_directory_nodes_with_aggregated_counts()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.ShowTree = true;

        // All four files live under C:\s — the single C: root aggregates the whole subtree, with a
        // disposition rollup for each status plus the destructive-action counts.
        DryRunTreeNode root = Assert.Single(viewModel.AffectedTree);
        Assert.Equal("C:", root.Name);
        Assert.True(root.IsDirectory);
        Assert.Equal(2, root.ProcessedCount);  // fresh.txt + clobber.txt
        Assert.Equal(1, root.FilteredCount);   // junk.tmp
        Assert.Equal(1, root.UnchangedCount);  // same.txt
        Assert.Equal(1, root.OverwriteCount);  // clobber.txt's overwrite target
        Assert.Equal(1, root.RenameCount);     // clobber.txt's rename target
        Assert.Equal(1, root.DisposalCount);   // clobber.txt's MoveToTrash
    }

    [Fact]
    public async Task Tree_reflects_the_visible_filtered_rows()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.ShowProcessed = false;   // hide the processed rows before building the tree
        viewModel.ShowTree = true;

        DryRunTreeNode root = Assert.Single(viewModel.AffectedTree);
        Assert.Equal(0, root.ProcessedCount);   // processed rows filtered out of the visible set
        Assert.Equal(1, root.FilteredCount);
        Assert.Equal(1, root.UnchangedCount);
    }

    [Fact]
    public async Task Tree_starts_with_only_the_roots_expanded_and_expansion_round_trips()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.ShowTree = true;

        // The built-in TreeView owns expand/collapse now; the VM just supplies the forest with the
        // top-level root expanded and deeper directories collapsed (children ready to realize on expand).
        DryRunTreeNode root = Assert.Single(viewModel.AffectedTree);
        Assert.True(root.IsExpanded);
        DryRunTreeNode sDir = Assert.Single(root.Children);
        Assert.Equal("s", sDir.Name);
        Assert.True(sDir.IsDirectory);
        Assert.False(sDir.IsExpanded);
        Assert.NotEmpty(sDir.Children);   // all four sample files live here

        // IsExpanded is observable so the TreeViewItem two-way binding round-trips.
        bool raised = false;
        sDir.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DryRunTreeNode.IsExpanded)) raised = true; };
        sDir.IsExpanded = true;
        Assert.True(raised);
    }

    [Fact]
    public async Task Report_defaults_to_list_view()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.ShowTree);
        Assert.Empty(viewModel.AffectedTree);
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
        Assert.False(viewModel.ShowDestructive);
        Assert.Equal(4, viewModel.AffectedFiles.Count);
        Assert.Equal(3, viewModel.AffectedFiles.Count(r => r.IsProcessed));
        Assert.Single(viewModel.AffectedFiles, r => r.IsFilterSkipped);

        viewModel.SourceFacets.Single(f => f.Root == @"C:\a").IsSelected = false;

        // Only C:\b's rows remain visible; C:\a's process row and filter-skip drop out.
        Assert.Equal(2, viewModel.AffectedFiles.Count);
        Assert.All(viewModel.AffectedFiles, r => Assert.Equal(@"C:\b", r.SourceRoot));
        Assert.DoesNotContain(viewModel.AffectedFiles, r => r.IsFilterSkipped);

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
