using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
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

    // ── New-model fixture helpers ──────────────────────────────────────────────────────────────
    // A dry-run report is now a bipartite graph: PhysicalFile nodes (SourceFiles / DestinationFiles)
    // plus VirtualFileOperation edges (SourceOperations / DestinationOperations) that reference files
    // by integer index. The VM pairs SourceFiles[i] with the source op whose SourceIndex == i, groups
    // destination ops by SourceIndex for that file's target rows, and maps every destination op 1:1 to
    // a destination row (SourceRoot resolved via SourceIndex).

    private static PhysicalFile Pf(string path, string root) =>
        new() { Path = path, Root = root, Length = 0, LastWritten = DateTimeOffset.UnixEpoch, IsReparsePoint = false };

    private static VirtualFileOperation SrcOp(
        int index, string path, string root, OperationKind kind,
        OnSuccessAction? disposition = null, string? detail = null) =>
        new() { Path = path, Root = root, Kind = kind, SourceIndex = index, SubjectIndex = -1, SourceDisposition = disposition, Detail = detail };

    private static VirtualFileOperation DstOp(
        OperationKind kind, string path, string root, int sourceIndex = -1, int subjectIndex = -1, string? detail = null) =>
        new() { Path = path, Root = root, Kind = kind, SourceIndex = sourceIndex, SubjectIndex = subjectIndex, Detail = detail };

    private static DryRunReport Report(
        Guid profileId,
        IReadOnlyList<PhysicalFile> sourceFiles,
        IReadOnlyList<VirtualFileOperation> sourceOps,
        IReadOnlyList<PhysicalFile> destinationFiles,
        IReadOnlyList<VirtualFileOperation> destinationOps,
        bool truncated = false) =>
        new()
        {
            ProfileId = profileId,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
            Truncated = truncated,
        };

    // fresh.txt: new write (KeepSource). clobber.txt: overwrites one target + renames around another
    // (the kept original is a destination-only Untouched op), and its source is trashed (processed AND
    // deleted). junk.tmp: filtered out. same.txt: unchanged.
    private static DryRunReport SampleReport(Guid profileId) => Report(profileId,
        sourceFiles:
        [
            Pf(@"C:\s\fresh.txt", @"C:\s"),     // 0
            Pf(@"C:\s\clobber.txt", @"C:\s"),   // 1
            Pf(@"C:\s\junk.tmp", @"C:\s"),      // 2
            Pf(@"C:\s\same.txt", @"C:\s"),      // 3
        ],
        sourceOps:
        [
            SrcOp(0, @"C:\s\fresh.txt", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(1, @"C:\s\clobber.txt", @"C:\s", OperationKind.Processed, OnSuccessAction.MoveToTrash),
            SrcOp(2, @"C:\s\junk.tmp", @"C:\s", OperationKind.SkippedByFilter, detail: "Exclude pattern glob *.tmp"),
            SrcOp(3, @"C:\s\same.txt", @"C:\s", OperationKind.SkippedUnchanged),
        ],
        destinationFiles:
        [
            Pf(@"C:\t\clobber.txt", @"C:\t"),    // 0 — the overwrite subject
            Pf(@"C:\t2\clobber.txt", @"C:\t2"),  // 1 — the rename kept-original subject
            Pf(@"C:\t\same.txt", @"C:\t"),       // 2 — the skip-unchanged subject
        ],
        destinationOps:
        [
            DstOp(OperationKind.New, @"C:\t\fresh.txt", @"C:\t", sourceIndex: 0),
            DstOp(OperationKind.Overwrite, @"C:\t\clobber.txt", @"C:\t", sourceIndex: 1, subjectIndex: 0),
            DstOp(OperationKind.Rename, @"C:\t2\clobber (1).txt", @"C:\t2", sourceIndex: 1, detail: "renamed to avoid a conflict"),
            DstOp(OperationKind.Untouched, @"C:\t2\clobber.txt", @"C:\t2", subjectIndex: 1, detail: "kept (an incoming file was renamed around it)"),
            DstOp(OperationKind.SkipUnchanged, @"C:\t\same.txt", @"C:\t", sourceIndex: 3, subjectIndex: 2),
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
    public async Task A_conflict_rename_keeps_the_original_as_a_destination_only_untouched_row()
    {
        // The kept-around original is emitted by the server as a destination op with SourceIndex == -1,
        // so it must NOT appear among the source file's target rows — clobber shows exactly its real
        // targets (Overwrite + Rename), and the kept original surfaces only in the Destinations view.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        DryRunFileRow clobber = viewModel.Sources.VisibleRows.Single(r => r.SourcePath.EndsWith("clobber.txt"));
        Assert.Equal(2, clobber.Targets.Count);
        Assert.Contains(clobber.Targets, t => t.IsOverwrite);
        Assert.Contains(clobber.Targets, t => t.IsRename);
        Assert.DoesNotContain(clobber.Targets, t => t.Path == @"C:\t2\clobber.txt");   // the kept original is not a target

        DryRunDestinationRow kept = viewModel.Destinations.VisibleRows.Single(r => r.TargetPath == @"C:\t2\clobber.txt");
        Assert.True(kept.IsUntouched);
        Assert.False(kept.HasSource);   // SourceIndex == -1
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
        gateway.DryRunResult = WritesReport(viewModel.ProfileId!.Value,
            (@"C:\src\a\z.txt", @"C:\src", @"D:\dst\a\z.txt", @"D:\dst"),
            (@"C:\src\a\a.txt", @"C:\src", @"D:\dst\a\a.txt", @"D:\dst"),
            (@"C:\src\m.txt", @"C:\src", @"D:\dst\m.txt", @"D:\dst"));

        await viewModel.RunAsync(CancellationToken.None);

        var sourceRel = viewModel.Sources.VisibleRows
            .Select(r => System.IO.Path.GetRelativePath(r.SourceRoot!, r.SourcePath)).ToList();
        var destRel = viewModel.Destinations.VisibleRows
            .Select(r => System.IO.Path.GetRelativePath(r.TargetRoot, r.TargetPath)).ToList();

        Assert.Equal(sourceRel, destRel);   // identical relative-path ordering in both tabs
    }

    // Builds a report of plain new-writes: one source file + one New destination op per write.
    private static DryRunReport WritesReport(Guid profileId, params (string Src, string SrcRoot, string Dst, string DstRoot)[] writes)
    {
        var sourceFiles = new List<PhysicalFile>();
        var sourceOps = new List<VirtualFileOperation>();
        var destinationOps = new List<VirtualFileOperation>();
        for (int i = 0; i < writes.Length; i++)
        {
            (string src, string srcRoot, string dst, string dstRoot) = writes[i];
            sourceFiles.Add(Pf(src, srcRoot));
            sourceOps.Add(SrcOp(i, src, srcRoot, OperationKind.Processed, OnSuccessAction.KeepSource));
            destinationOps.Add(DstOp(OperationKind.New, dst, dstRoot, sourceIndex: i));
        }
        return Report(profileId, sourceFiles, sourceOps, [], destinationOps);
    }

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
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles: [Pf(@"C:\s\a.txt", @"C:\s")],
            sourceOps: [SrcOp(0, @"C:\s\a.txt", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource)],
            destinationFiles:
            [
                Pf(@"C:\t\keep.txt", @"C:\t"),      // 0 — pre-existing, untouched
                Pf(@"C:\t\orphan.txt", @"C:\t"),    // 1 — Mirror orphan
            ],
            destinationOps:
            [
                DstOp(OperationKind.New, @"C:\t\a.txt", @"C:\t", sourceIndex: 0),
                DstOp(OperationKind.Untouched, @"C:\t\keep.txt", @"C:\t", subjectIndex: 0),
                DstOp(OperationKind.Deleted, @"C:\t\orphan.txt", @"C:\t", subjectIndex: 1),
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

    // Sources nested one level under a single root, so the tree's top level is the sub-folder (not
    // the drive) and that node rolls up the counts beneath it.
    private static DryRunReport NestedSourcesReport(Guid profileId) => Report(profileId,
        sourceFiles:
        [
            Pf(@"C:\proj\sub\one.txt", @"C:\proj"),
            Pf(@"C:\proj\sub\two.tmp", @"C:\proj"),
            Pf(@"C:\proj\sub\three.txt", @"C:\proj"),
        ],
        sourceOps:
        [
            SrcOp(0, @"C:\proj\sub\one.txt", @"C:\proj", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(1, @"C:\proj\sub\two.tmp", @"C:\proj", OperationKind.SkippedByFilter, detail: "exclude *.tmp"),
            SrcOp(2, @"C:\proj\sub\three.txt", @"C:\proj", OperationKind.Processed, OnSuccessAction.MoveToTrash),
        ],
        destinationFiles: [],
        destinationOps: []);

    [Fact]
    public async Task Sources_tree_starts_at_the_common_root_and_rolls_up_pills()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Sources.ShowTree = true;

        // Top level is "sub" (under C:\proj), not the "C:" drive — no click-through.
        DryRunTreeNode sub = Assert.Single(viewModel.Sources.Tree);
        Assert.Equal("sub", sub.Name);
        Assert.True(sub.IsDirectory);
        Assert.True(sub.IsExpanded);
        Assert.Equal(@"C:\proj\sub", sub.FullPath);   // FullPath stays absolute for the tooltip
        var texts = sub.Pills.Select(p => p.Text).ToList();
        Assert.Contains("1 untouched", texts);
        Assert.Contains("2 processed", texts);
        Assert.Contains("1 deleted", texts);
    }

    // Destinations nested one level under a single target root.
    private static DryRunReport NestedDestinationsReport(Guid profileId) => Report(profileId,
        sourceFiles:
        [
            Pf(@"C:\in\fresh.txt", @"C:\in"),
            Pf(@"C:\in\clob.txt", @"C:\in"),
        ],
        sourceOps:
        [
            SrcOp(0, @"C:\in\fresh.txt", @"C:\in", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(1, @"C:\in\clob.txt", @"C:\in", OperationKind.Processed, OnSuccessAction.KeepSource),
        ],
        destinationFiles: [Pf(@"C:\out\sub\clob.txt", @"C:\out"), Pf(@"C:\out\sub\keep.txt", @"C:\out")],
        destinationOps:
        [
            DstOp(OperationKind.New, @"C:\out\sub\fresh.txt", @"C:\out", sourceIndex: 0),
            DstOp(OperationKind.Overwrite, @"C:\out\sub\clob.txt", @"C:\out", sourceIndex: 1, subjectIndex: 0),
            DstOp(OperationKind.Untouched, @"C:\out\sub\keep.txt", @"C:\out", subjectIndex: 1),
        ]);

    [Fact]
    public async Task Destinations_tree_starts_at_the_common_root_and_rolls_up_pills()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = NestedDestinationsReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Destinations.ShowTree = true;

        DryRunTreeNode sub = Assert.Single(viewModel.Destinations.Tree);
        Assert.Equal("sub", sub.Name);
        Assert.Equal(@"C:\out\sub", sub.FullPath);
        var texts = sub.Pills.Select(p => p.Text).ToList();
        Assert.Contains("1 new", texts);
        Assert.Contains("1 overwritten", texts);
        Assert.Contains("1 untouched", texts);
    }

    [Fact]
    public async Task Common_root_is_the_single_source_directory()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);   // all under C:\s → C:\t/C:\t2
        await viewModel.RunAsync(CancellationToken.None);

        Assert.Equal(@"C:\s", viewModel.Sources.CommonRoot);
        Assert.Contains("Relative to", viewModel.Sources.CommonRootDisplay);
        Assert.Equal(@"C:\", viewModel.Destinations.CommonRoot);   // C:\t and C:\t2 share only the drive
    }

    [Fact]
    public async Task Common_root_is_null_and_paths_stay_absolute_when_sources_span_drives()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = WritesReport(viewModel.ProfileId!.Value,
            (@"C:\a\one.txt", @"C:\a", @"E:\out\one.txt", @"E:\out"),
            (@"D:\b\two.txt", @"D:\b", @"E:\out\two.txt", @"E:\out"));
        await viewModel.RunAsync(CancellationToken.None);

        Assert.Null(viewModel.Sources.CommonRoot);
        Assert.Contains("Multiple drives", viewModel.Sources.CommonRootDisplay);
        // With no common root the parent display is the absolute directory.
        DryRunFileRow row = viewModel.Sources.VisibleRows.First(r => r.SourcePath == @"C:\a\one.txt");
        Assert.Equal(@"C:\a\", row.ParentDisplay);
    }

    [Fact]
    public async Task Rows_carry_a_parent_display_relative_to_the_common_root()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        DryRunFileRow row = viewModel.Sources.VisibleRows.First();
        Assert.Equal(@"sub\", row.ParentDisplay);
        Assert.Equal("one.txt", row.FileName);
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
    private static DryRunReport MultiSourceReport(Guid profileId) => Report(profileId,
        sourceFiles:
        [
            Pf(@"C:\a\one.txt", @"C:\a"),     // 0
            Pf(@"C:\a\junk.tmp", @"C:\a"),    // 1
            Pf(@"C:\b\three.txt", @"C:\b"),   // 2
            Pf(@"C:\b\four.txt", @"C:\b"),    // 3
        ],
        sourceOps:
        [
            SrcOp(0, @"C:\a\one.txt", @"C:\a", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(1, @"C:\a\junk.tmp", @"C:\a", OperationKind.SkippedByFilter, detail: "exclude *.tmp"),
            SrcOp(2, @"C:\b\three.txt", @"C:\b", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(3, @"C:\b\four.txt", @"C:\b", OperationKind.Processed, OnSuccessAction.KeepSource),
        ],
        destinationFiles: [],
        destinationOps:
        [
            DstOp(OperationKind.New, @"C:\t\one.txt", @"C:\t", sourceIndex: 0),
            DstOp(OperationKind.New, @"C:\t\three.txt", @"C:\t", sourceIndex: 2),
            DstOp(OperationKind.New, @"C:\t\four.txt", @"C:\t", sourceIndex: 3),
        ]);
}
