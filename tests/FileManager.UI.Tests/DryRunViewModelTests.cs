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

    // fresh.txt: new write (KeepSource). clobber.txt: overwrites one target + renames around another,
    // and its source is trashed (processed AND deleted). junk.tmp: filtered out. same.txt: unchanged.
    private static DryRunReport SampleReport(Guid profileId) => new(profileId, DateTimeOffset.UnixEpoch,
    [
        new DryRunFileResult
        {
            SourcePath = @"C:\s\fresh.txt", SourceRoot = @"C:\s",
            Disposition = DryRunFileDisposition.WouldProcess,
            SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\fresh.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldWrite }],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\clobber.txt", SourceRoot = @"C:\s",
            Disposition = DryRunFileDisposition.WouldProcess,
            SourceDisposition = "MoveToTrash",
            Targets =
            [
                new DryRunTargetAction { TargetPath = @"C:\t\clobber.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldOverwrite },
                new DryRunTargetAction { TargetPath = @"C:\t2\clobber.txt", TargetRoot = @"C:\t2", Kind = DryRunTargetKind.WouldRenameTo, Detail = @"C:\t2\clobber (1).txt" },
            ],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\junk.tmp", SourceRoot = @"C:\s",
            Disposition = DryRunFileDisposition.WouldSkipFilter,
            DecidingFilter = "Exclude pattern glob *.tmp",
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\same.txt", SourceRoot = @"C:\s",
            Disposition = DryRunFileDisposition.WouldSkipUnchanged,
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\same.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldSkipUnchanged }],
        },
    ]);

    [Fact]
    public async Task Blast_radius_banner_reflects_the_report()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.HasReport);
        Assert.Equal(4, viewModel.TotalFiles);
        Assert.Equal(1, viewModel.OverwriteCount);
        Assert.Equal(1, viewModel.RenameCount);
        Assert.Equal(1, viewModel.DisposalCount);   // MoveToTrash; KeepSource is not a disposal
        Assert.True(viewModel.HasDestructiveActions);
    }

    [Fact]
    public async Task Sources_tab_counts_split_untouched_processed_deleted()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        // Untouched = filtered-out + unchanged (junk.tmp + same.txt).
        Assert.Equal(2, viewModel.Sources.UntouchedCount);
        Assert.Equal(2, viewModel.Sources.ProcessedCount);   // fresh + clobber
        Assert.Equal(1, viewModel.Sources.DeletedCount);     // clobber (MoveToTrash)
        Assert.Equal(4, viewModel.Sources.VisibleRows.Count);
    }

    [Fact]
    public async Task A_processed_and_deleted_source_carries_both_pills()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        DryRunFileRow clobber = viewModel.Sources.VisibleRows.Single(r => r.SourcePath.EndsWith("clobber.txt"));
        Assert.True(clobber.IsProcessed);
        Assert.True(clobber.IsDeleted);
        Assert.False(clobber.IsUntouched);
    }

    [Fact]
    public async Task Sources_rows_sort_by_path()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        var paths = viewModel.Sources.VisibleRows.Select(r => r.SourcePath).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), paths);
    }

    [Fact]
    public async Task Sources_and_destinations_share_relative_path_ordering()
    {
        // Distinct source/target roots and paths that would sort differently by full path but must
        // line up by path-relative-to-root so the two previews are comparable row-for-row.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new DryRunReport(viewModel.ProfileId!.Value, DateTimeOffset.UnixEpoch,
            [
                Write(@"C:\src\a\z.txt", @"C:\src", @"D:\dst\a\z.txt", @"D:\dst"),
                Write(@"C:\src\a\a.txt", @"C:\src", @"D:\dst\a\a.txt", @"D:\dst"),
                Write(@"C:\src\m.txt", @"C:\src", @"D:\dst\m.txt", @"D:\dst"),
            ]);

        await viewModel.RunAsync(CancellationToken.None);

        var sourceRel = viewModel.Sources.VisibleRows
            .Select(r => System.IO.Path.GetRelativePath(r.SourceRoot!, r.SourcePath)).ToList();
        var destRel = viewModel.Destinations.VisibleRows
            .Select(r => System.IO.Path.GetRelativePath(r.TargetRoot, r.TargetPath)).ToList();

        Assert.Equal(sourceRel, destRel);   // identical relative-path ordering in both tabs
    }

    private static DryRunFileResult Write(string sourcePath, string sourceRoot, string targetPath, string targetRoot) => new()
    {
        SourcePath = sourcePath, SourceRoot = sourceRoot,
        Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
        Targets = [new DryRunTargetAction { TargetPath = targetPath, TargetRoot = targetRoot, Kind = DryRunTargetKind.WouldWrite }],
    };

    [Fact]
    public async Task Destinations_tab_derives_new_overwritten_and_untouched_from_targets()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // fresh (New) + clobber's renamed suffix path (New) = 2; clobber overwrite = 1;
        // clobber's original path kept + same.txt unchanged = 2 Untouched.
        Assert.Equal(2, viewModel.Destinations.NewCount);
        Assert.Equal(1, viewModel.Destinations.OverwrittenCount);
        Assert.Equal(2, viewModel.Destinations.UntouchedCount);
        Assert.Equal(0, viewModel.Destinations.DeletedCount);

        Assert.Contains(viewModel.Destinations.VisibleRows, r => r.TargetPath == @"C:\t2\clobber (1).txt" && r.IsNew);
        Assert.Contains(viewModel.Destinations.VisibleRows, r => r.TargetPath == @"C:\t2\clobber.txt" && r.IsUntouched);
    }

    [Fact]
    public async Task Destinations_tab_shows_preexisting_and_mirror_orphans_from_report_extras()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new DryRunReport(viewModel.ProfileId!.Value, DateTimeOffset.UnixEpoch,
            [
                new DryRunFileResult
                {
                    SourcePath = @"C:\s\a.txt", SourceRoot = @"C:\s",
                    Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
                    Targets = [new DryRunTargetAction { TargetPath = @"C:\t\a.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldWrite }],
                },
            ],
            Destinations:
            [
                new DryRunDestinationEntry { TargetPath = @"C:\t\keep.txt", TargetRoot = @"C:\t", Disposition = DryRunDestinationDisposition.Untouched },
                new DryRunDestinationEntry { TargetPath = @"C:\t\orphan.txt", TargetRoot = @"C:\t", Disposition = DryRunDestinationDisposition.Deleted },
            ]);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.Equal(1, viewModel.Destinations.NewCount);
        Assert.Equal(1, viewModel.Destinations.DeletedCount);
        Assert.Equal(1, viewModel.Destinations.UntouchedCount);
        Assert.True(viewModel.HasDestructiveActions);
        DryRunDestinationRow orphan = viewModel.Destinations.VisibleRows.Single(r => r.IsDeleted);
        Assert.False(orphan.HasSource);                     // extras have no originating source
    }

    [Fact]
    public async Task Sources_tab_filters_by_destination_root()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // Two target roots (C:\t, C:\t2) so the destination facet appears in the Sources tab.
        Assert.True(viewModel.Sources.ShowDestinationFacet);
        Assert.Equal(2, viewModel.Sources.DestinationFacets.Count);

        // Keep only C:\t2 — only clobber.txt lands there.
        viewModel.Sources.DestinationFacets.Single(f => f.Key == @"C:\t").IsSelected = false;
        DryRunFileRow row = Assert.Single(viewModel.Sources.VisibleRows);
        Assert.EndsWith("clobber.txt", row.SourcePath);
    }

    [Fact]
    public async Task Destinations_tab_filters_by_destination_root()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.Destinations.ShowDestinationFacet);
        viewModel.Destinations.DestinationFacets.Single(f => f.Key == @"C:\t").IsSelected = false;
        Assert.All(viewModel.Destinations.VisibleRows, r => Assert.Equal(@"C:\t2", r.TargetRoot));
    }

    [Fact]
    public async Task Source_facet_lists_each_distinct_source()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = MultiSourceReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.Sources.ShowSourceFacet);
        Assert.Equal(2, viewModel.Sources.SourceFacets.Count);
        Assert.All(viewModel.Sources.SourceFacets, f => Assert.True(f.IsSelected));

        viewModel.Sources.SourceFacets.Single(f => f.Key == @"C:\a").IsSelected = false;
        Assert.All(viewModel.Sources.VisibleRows, r => Assert.Equal(@"C:\b", r.SourceRoot));
        // A view filter never changes the whole-run banner.
        Assert.Equal(4, viewModel.TotalFiles);
    }

    [Fact]
    public async Task Single_source_report_shows_no_source_facet()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);   // all under C:\s
        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.Sources.ShowSourceFacet);
        Assert.Empty(viewModel.Sources.SourceFacets);
    }

    [Fact]
    public async Task Sources_search_matches_source_and_target_paths()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Sources.SearchText = "fresh";
        Assert.EndsWith("fresh.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);

        viewModel.Sources.SearchText = @"t2\clobber";   // only clobber has a t2 target
        Assert.EndsWith("clobber.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);

        viewModel.Sources.SearchText = "";
        Assert.Equal(4, viewModel.Sources.VisibleRows.Count);
    }

    [Fact]
    public async Task Sources_tree_rolls_up_untouched_processed_deleted_pills()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Sources.ShowTree = true;

        DryRunTreeNode root = Assert.Single(viewModel.Sources.Tree);
        Assert.Equal("C:", root.Name);
        Assert.True(root.IsDirectory);
        Assert.True(root.IsExpanded);
        var texts = root.Pills.Select(p => p.Text).ToList();
        Assert.Contains("2 untouched", texts);
        Assert.Contains("2 processed", texts);
        Assert.Contains("1 deleted", texts);
    }

    [Fact]
    public async Task Destinations_tree_rolls_up_new_overwritten_untouched_pills()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Destinations.ShowTree = true;

        DryRunTreeNode root = Assert.Single(viewModel.Destinations.Tree);
        var texts = root.Pills.Select(p => p.Text).ToList();
        Assert.Contains("2 new", texts);
        Assert.Contains("1 overwritten", texts);
        Assert.Contains("2 untouched", texts);
    }

    [Fact]
    public async Task Report_defaults_to_list_view_in_both_tabs()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.Sources.ShowTree);
        Assert.Empty(viewModel.Sources.Tree);
        Assert.False(viewModel.Destinations.ShowTree);
        Assert.Empty(viewModel.Destinations.Tree);
    }

    [Fact]
    public async Task Truncated_report_surfaces_a_notice_mentioning_deletions_are_hidden()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with { Truncated = true };

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.WasTruncated);
        Assert.Contains("truncated", viewModel.TruncationNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deletions", viewModel.TruncationNotice, StringComparison.OrdinalIgnoreCase);

        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);
        Assert.False(viewModel.WasTruncated);
        Assert.Equal("", viewModel.TruncationNotice);
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

    // Two sources (C:\a with one process + one filter-skip, C:\b with two process).
    private static DryRunReport MultiSourceReport(Guid profileId) => new(profileId, DateTimeOffset.UnixEpoch,
    [
        new DryRunFileResult
        {
            SourcePath = @"C:\a\one.txt", SourceRoot = @"C:\a",
            Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\one.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldWrite }],
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
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\three.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldWrite }],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\b\four.txt", SourceRoot = @"C:\b",
            Disposition = DryRunFileDisposition.WouldProcess, SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\four.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldWrite }],
        },
    ]);
}
