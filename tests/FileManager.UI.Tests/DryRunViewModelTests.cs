using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
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
    // A dry-run report is now a bipartite graph: DryRunFile nodes (SourceFiles / DestinationFiles)
    // plus DryRunOperation edges (SourceOperations / DestinationOperations) that reference files
    // by integer index. The VM pairs SourceFiles[i] with the source op whose SourceIndex == i, and
    // groups destination ops by SourceIndex: each source file becomes ONE destination row listing its
    // fan-out of DryRunDestinationEntry targets. Ops with SourceIndex == -1 (kept-around originals,
    // Mirror orphans, pre-existing untouched files) become their own single-entry, no-source rows.
    // Paths are normalized through a per-test directory table: the helpers keep their (path, root)
    // string signatures and convert through the builder (xunit news the class up per test, so one
    // builder spans exactly one report's index space).

    private readonly DryRunDirectoryTableBuilder _dirs = new();

    private DryRunFile Pf(string path, string root) =>
        _dirs.Convert(new PhysicalFile { Path = path, Root = root, Length = 0, LastWritten = DateTimeOffset.UnixEpoch, IsReparsePoint = false });

    private DryRunOperation SrcOp(
        int index, string path, string root, OperationKind kind,
        OnSuccessAction? disposition = null, string? detail = null) =>
        _dirs.Convert(new VirtualFileOperation { Path = path, Root = root, Kind = kind, SourceIndex = index, SubjectIndex = -1, SourceDisposition = disposition, Detail = detail });

    private DryRunOperation DstOp(
        OperationKind kind, string path, string root, int sourceIndex = -1, int subjectIndex = -1, string? detail = null) =>
        _dirs.Convert(new VirtualFileOperation { Path = path, Root = root, Kind = kind, SourceIndex = sourceIndex, SubjectIndex = subjectIndex, Detail = detail });

    private DryRunReport Report(
        Guid profileId,
        IReadOnlyList<DryRunFile> sourceFiles,
        IReadOnlyList<DryRunOperation> sourceOps,
        IReadOnlyList<DryRunFile> destinationFiles,
        IReadOnlyList<DryRunOperation> destinationOps,
        bool truncated = false) =>
        new()
        {
            ProfileId = profileId,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = _dirs.Entries.ToList(),
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
            Truncated = truncated,
        };

    // fresh.txt: new write (KeepSource). clobber.txt: overwrites one target + renames around another
    // (the kept original is a destination-only Untouched op), and its source is trashed (processed AND
    // deleted). junk.tmp: filtered out. same.txt: unchanged.
    private DryRunReport SampleReport(Guid profileId) => Report(profileId,
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

        DryRunDestinationRow kept = viewModel.Destinations.VisibleRows
            .Single(r => !r.HasSource && r.Primary.TargetPath == @"C:\t2\clobber.txt");
        Assert.True(kept.Primary.IsUntouched);
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
            .Select(r => System.IO.Path.GetRelativePath(r.Primary.TargetRoot, r.Primary.TargetPath)).ToList();

        Assert.Equal(sourceRel, destRel);   // identical relative-path ordering in both tabs
    }

    // Builds a report of plain new-writes: one source file + one New destination op per write.
    private DryRunReport WritesReport(Guid profileId, params (string Src, string SrcRoot, string Dst, string DstRoot)[] writes)
    {
        var sourceFiles = new List<DryRunFile>();
        var sourceOps = new List<DryRunOperation>();
        var destinationOps = new List<DryRunOperation>();
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

        var entries = viewModel.Destinations.VisibleRows.SelectMany(r => r.Destinations).ToList();
        Assert.Contains(entries, e => e.TargetPath == @"C:\t2\clobber (1).txt" && e.IsNew);
        Assert.Contains(entries, e => e.TargetPath == @"C:\t2\clobber.txt" && e.IsUntouched);
    }

    [Fact]
    public async Task Replicated_file_collapses_to_one_row_listing_each_destination()
    {
        // One source fanned out to three targets → a single grouped row, not three rows.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles: [Pf(@"C:\s\report.docx", @"C:\s")],
            sourceOps: [SrcOp(0, @"C:\s\report.docx", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource)],
            destinationFiles: [],
            destinationOps:
            [
                DstOp(OperationKind.New, @"C:\a\report.docx", @"C:\a", sourceIndex: 0),
                DstOp(OperationKind.New, @"C:\b\report.docx", @"C:\b", sourceIndex: 0),
                DstOp(OperationKind.Overwrite, @"C:\c\report.docx", @"C:\c", sourceIndex: 0),
            ]);
        await viewModel.RunAsync(CancellationToken.None);

        DryRunDestinationRow row = Assert.Single(viewModel.Destinations.VisibleRows);
        Assert.True(row.HasSource);
        Assert.Equal(@"C:\s\report.docx", row.SourcePath);
        Assert.Equal(3, row.Destinations.Count);
        Assert.Equal(2, row.Destinations.Count(d => d.IsNew));
        Assert.Equal(1, row.Destinations.Count(d => d.IsOverwritten));
        // Counts remain over the individual destinations, not the collapsed row.
        Assert.Equal(2, viewModel.Destinations.NewCount);
        Assert.Equal(1, viewModel.Destinations.OverwrittenCount);
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
        DryRunDestinationRow orphan = viewModel.Destinations.VisibleRows.Single(r => r.Destinations.Any(d => d.IsDeleted));
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
        Assert.All(viewModel.Destinations.VisibleRows.SelectMany(r => r.Destinations),
            e => Assert.Equal(@"C:\t2", e.TargetRoot));
    }

    [Fact]
    public async Task Sources_status_filter_narrows_to_selected_statuses()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // No selection → the list is unfiltered.
        Assert.False(viewModel.Sources.AnyStatusSelected);
        Assert.Equal(4, viewModel.Sources.VisibleRows.Count);

        // Deleted → only clobber.txt (the one trashed source).
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = true;
        Assert.True(viewModel.Sources.AnyStatusSelected);
        Assert.EndsWith("clobber.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);

        // Untouched → the filtered-out + unchanged files (junk.tmp + same.txt).
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = false;
        viewModel.Sources.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        Assert.Equal(2, viewModel.Sources.VisibleRows.Count);
        Assert.All(viewModel.Sources.VisibleRows, r => Assert.True(r.IsUntouched));

        // A view filter never changes the whole-run summary counts.
        Assert.Equal(1, viewModel.Sources.DeletedCount);
        Assert.Equal(2, viewModel.Sources.UntouchedCount);
    }

    [Fact]
    public async Task Sources_status_filters_union_and_clear_restores_all()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // Processed OR Deleted — clobber.txt carries both, so the union is fresh + clobber (a
        // both-statuses row shows once, not twice).
        viewModel.Sources.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = true;
        var names = viewModel.Sources.VisibleRows.Select(r => r.FileName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("fresh.txt", names);
        Assert.Contains("clobber.txt", names);

        // The clear command drops every selection and shows everything again.
        viewModel.Sources.ClearStatusFiltersCommand.Execute(null);
        Assert.False(viewModel.Sources.AnyStatusSelected);
        Assert.All(viewModel.Sources.StatusFilters, f => Assert.False(f.IsSelected));
        Assert.Equal(4, viewModel.Sources.VisibleRows.Count);
    }

    [Fact]
    public async Task Destinations_status_filter_prunes_entries_and_keeps_counts()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // New only: keeps fresh.txt and clobber.txt's rename target, dropping clobber's Overwrite
        // entry and the untouched rows entirely.
        viewModel.Destinations.StatusFilters.Single(f => f.Key == "new").IsSelected = true;
        var entries = viewModel.Destinations.VisibleRows.SelectMany(r => r.Destinations).ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.IsNew));

        // Counts stay over the whole run, independent of the filtered view.
        Assert.Equal(2, viewModel.Destinations.NewCount);
        Assert.Equal(1, viewModel.Destinations.OverwrittenCount);
        Assert.Equal(2, viewModel.Destinations.UntouchedCount);
    }

    [Fact]
    public async Task Status_filter_and_search_combine()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // Processed matches fresh + clobber; the search AND-restricts it to fresh.
        viewModel.Sources.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        viewModel.Sources.SearchText = "fresh";
        Assert.EndsWith("fresh.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);
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
    private DryRunReport NestedSourcesReport(Guid profileId) => Report(profileId,
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
        Assert.Contains(sub.Pills, p => p.Tip == "untouched" && p.CountText == "1");
        Assert.Contains(sub.Pills, p => p.Tip == "processed" && p.CountText == "2");
        Assert.Contains(sub.Pills, p => p.Tip == "deleted" && p.CountText == "1");
    }

    // Destinations nested one level under a single target root.
    private DryRunReport NestedDestinationsReport(Guid profileId) => Report(profileId,
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
        Assert.Contains(sub.Pills, p => p.Tip == "new" && p.CountText == "1");
        Assert.Contains(sub.Pills, p => p.Tip == "overwritten" && p.CountText == "1");
        Assert.Contains(sub.Pills, p => p.Tip == "untouched" && p.CountText == "1");
    }

    [Fact]
    public async Task Tree_reconstructs_UNC_paths_when_roots_span_shares()
    {
        // Two unrelated UNC shares → no common root, so the tree falls back to splitting the raw
        // absolute path. The leading "\\" must survive that split/rejoin (regression: it was dropped,
        // yielding "srv1\share" tooltips instead of "\\srv1\share").
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = WritesReport(viewModel.ProfileId!.Value,
            (@"\\srv1\share\a.txt", @"\\srv1\share", @"\\dst\out\a.txt", @"\\dst\out"),
            (@"\\srv2\other\b.txt", @"\\srv2\other", @"\\dst\out\b.txt", @"\\dst\out"));
        await viewModel.RunAsync(CancellationToken.None);

        Assert.Null(viewModel.Sources.CommonRoot);   // unrelated shares span no shared prefix
        viewModel.Sources.ShowTree = true;

        DryRunTreeNode srv1 = viewModel.Sources.Tree.Single(n => n.Name == "srv1");
        Assert.Equal(@"\\srv1", srv1.FullPath);
        DryRunTreeNode share = srv1.Children.Single(n => n.Name == "share");
        Assert.Equal(@"\\srv1\share", share.FullPath);
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
    public async Task Re_running_a_dry_run_releases_the_previous_tree_forest()
    {
        // Regression: Load() forces ShowTree=false under the _applying guard, so the ShowTree setter
        // won't clear a forest built by a prior run. Without an explicit Tree reset the previous
        // report's entire forest (up to the streamed cap) would stay retained until the next manual
        // toggle — the exact retained memory the optimization work set out to eliminate.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Sources.ShowTree = true;
        Assert.NotEmpty(viewModel.Sources.Tree);
        Assert.NotNull(viewModel.Sources.TreeSource);

        // A second run applies a fresh report without the user toggling the tree off first.
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.Sources.ShowTree);
        Assert.Empty(viewModel.Sources.Tree);
        Assert.Null(viewModel.Sources.TreeSource);
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

    [Fact]
    public async Task File_and_destination_rows_carry_formatted_sizes()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles: [_dirs.Convert(new PhysicalFile { Path = @"C:\s\a.dat", Root = @"C:\s", Length = 1536, LastWritten = DateTimeOffset.UnixEpoch })],
            sourceOps: [SrcOp(0, @"C:\s\a.dat", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource)],
            destinationFiles: [],
            destinationOps: [DstOp(OperationKind.New, @"D:\d\a.dat", @"D:\d", sourceIndex: 0)]);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.Equal("1.5 KB", Assert.Single(viewModel.Sources.VisibleRows).SizeText);
        var entry = viewModel.Destinations.VisibleRows.SelectMany(r => r.Destinations).Single();
        Assert.Equal("1.5 KB", entry.SizeText);   // the incoming content size
    }

    [Fact]
    public async Task Space_projection_populates_volume_rows_with_state_driven_warnings()
    {
        var (viewModel, gateway) = NewViewModel();
        var space = new SpaceProjection
        {
            TotalBytesWritten = 5000,
            TotalNetChangeBytes = 4000,
            SafetyMarginBytes = 100,
            Volumes =
            [
                // peak (950) crosses capacity − margin (900) → danger.
                new VolumeSpaceEstimate
                {
                    VolumeRoot = "D:", CapacityKnown = true, TotalCapacityBytes = 1000, UsedNowBytes = 500, FreeNowBytes = 500,
                    ClusterBytes = 1, BytesWrittenBytes = 5000, NetChangeBytes = 400, SettledUsedBytes = 900,
                    RealisticPeakUsedBytes = 950, SafeCeilingUsedBytes = 980,
                },
                // everything well under capacity − margin (1900) → calm.
                new VolumeSpaceEstimate
                {
                    VolumeRoot = "E:", CapacityKnown = true, TotalCapacityBytes = 2000, UsedNowBytes = 100, FreeNowBytes = 1900,
                    ClusterBytes = 1, BytesWrittenBytes = 100, NetChangeBytes = 100, SettledUsedBytes = 200,
                    RealisticPeakUsedBytes = 300, SafeCeilingUsedBytes = 500,
                },
            ],
        };
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with { Space = space };

        await viewModel.RunAsync(CancellationToken.None);

        Assert.NotNull(viewModel.Space);
        Assert.StartsWith("+", viewModel.Space!.NetChangeText);
        Assert.Equal(2, viewModel.Space.Volumes.Count);

        VolumeSpaceRow d = viewModel.Space.Volumes.Single(v => v.VolumeRoot == "D:");
        Assert.True(d.IsDanger);
        Assert.True(d.HasWarning);
        Assert.Equal(1000, d.CapacityBytes);
        Assert.Equal(950, d.RealisticPeakBytes);

        // Header labels reflect the CURRENT drive state; SUAR reflects the settled after-run total.
        Assert.Equal(ByteSize.Format(500), d.UsedNowText);
        Assert.Equal(ByteSize.Format(500), d.CurrentFreeText);
        Assert.Equal(ByteSize.Format(900), d.SettledUsedText);

        VolumeSpaceRow e = viewModel.Space.Volumes.Single(v => v.VolumeRoot == "E:");
        Assert.False(e.HasWarning);
    }

    [Fact]
    public async Task Space_is_null_when_the_report_carries_no_projection()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);   // Space defaults null
        await viewModel.RunAsync(CancellationToken.None);
        Assert.Null(viewModel.Space);
    }

    // Two sources (C:\a with one process + one filter-skip, C:\b with two process).
    private DryRunReport MultiSourceReport(Guid profileId) => Report(profileId,
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

    // Sources nested two directory levels under a single root, so the tree has an interior folder
    // ("deep") below the auto-expanded top level — a node whose expansion the user can toggle.
    private DryRunReport DeepSourcesReport(Guid profileId) => Report(profileId,
        sourceFiles:
        [
            Pf(@"C:\proj\sub\deep\one.txt", @"C:\proj"),
            Pf(@"C:\proj\sub\deep\two.txt", @"C:\proj"),
            Pf(@"C:\proj\sub\other\three.txt", @"C:\proj"),
        ],
        sourceOps:
        [
            SrcOp(0, @"C:\proj\sub\deep\one.txt", @"C:\proj", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(1, @"C:\proj\sub\deep\two.txt", @"C:\proj", OperationKind.Processed, OnSuccessAction.KeepSource),
            SrcOp(2, @"C:\proj\sub\other\three.txt", @"C:\proj", OperationKind.Processed, OnSuccessAction.KeepSource),
        ],
        destinationFiles: [],
        destinationOps: []);

    [Fact]
    public async Task Duplicate_source_index_degrades_gracefully_instead_of_wiping_the_report()
    {
        // A service-side bug can emit two source ops with the same SourceIndex. The VM now groups and
        // keeps the first rather than throwing on a duplicate dictionary key (which would blank the
        // whole preview behind an error banner).
        var (viewModel, gateway) = NewViewModel();
        Guid pid = viewModel.ProfileId!.Value;
        gateway.DryRunResult = Report(pid,
            sourceFiles:
            [
                Pf(@"C:\s\a.txt", @"C:\s"),
                Pf(@"C:\s\b.txt", @"C:\s"),
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\s\a.txt", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(0, @"C:\s\a.txt", @"C:\s", OperationKind.SkippedByFilter),   // duplicate SourceIndex 0
                SrcOp(1, @"C:\s\b.txt", @"C:\s", OperationKind.Processed, OnSuccessAction.KeepSource),
            ],
            destinationFiles: [],
            destinationOps:
            [
                DstOp(OperationKind.New, @"C:\t\a.txt", @"C:\t", sourceIndex: 0),
                DstOp(OperationKind.New, @"C:\t\b.txt", @"C:\t", sourceIndex: 1),
            ]);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.HasReport);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(2, viewModel.Sources.VisibleRows.Count);
    }

    [Fact]
    public async Task Tree_expansion_persists_across_a_search_driven_rebuild()
    {
        var (viewModel, gateway) = NewViewModel();   // searchDebounce == Zero → rebuilds are synchronous
        gateway.DryRunResult = DeepSourcesReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.Sources.ShowTree = true;

        DryRunTreeNode sub = Assert.Single(viewModel.Sources.Tree);   // top-level "sub", auto-expanded
        DryRunTreeNode deep = sub.Children.Single(n => n.Name == "deep");
        Assert.False(deep.IsExpanded);   // interior nodes start collapsed
        deep.IsExpanded = true;

        // A search term that still matches the deep node's files forces a rebuild of the forest.
        viewModel.Sources.SearchText = "txt";

        DryRunTreeNode subAfter = Assert.Single(viewModel.Sources.Tree);
        DryRunTreeNode deepAfter = subAfter.Children.Single(n => n.Name == "deep");
        Assert.True(deepAfter.IsExpanded);   // expansion (keyed by FullPath) survived the rebuild
    }

    [Fact]
    public async Task Nonzero_debounce_coalesces_rapid_search_changes_to_the_final_term()
    {
        var tab = new DryRunSourcesTab(TimeSpan.FromMilliseconds(60));
        List<DryRunFileRow> rows =
        [
            new(@"C:\s", "alpha.txt", @"C:\s", OperationKind.Processed, null, "KeepSource", []),
            new(@"C:\s", "beta.txt", @"C:\s", OperationKind.Processed, null, "KeepSource", []),
            new(@"C:\s", "gamma.txt", @"C:\s", OperationKind.Processed, null, "KeepSource", []),
        ];
        tab.Load(rows, @"C:\s");
        Assert.Equal(3, tab.VisibleRows.Count);

        tab.SearchText = "alpha";
        tab.SearchText = "beta";
        tab.SearchText = "gamma";

        // The debounce delays the rebuild, so the intermediate terms have not been applied yet.
        Assert.Equal(3, tab.VisibleRows.Count);

        // After the window lapses exactly one rebuild lands, reflecting only the final term.
        await WaitUntilAsync(() =>
            tab.VisibleRows.Count == 1 && tab.VisibleRows[0].SourcePath.EndsWith("gamma.txt"));

        DryRunFileRow only = Assert.Single(tab.VisibleRows);
        Assert.EndsWith("gamma.txt", only.SourcePath);
    }

    // Enough rows to cross DryRunRebuild.SyncThreshold, so rebuilds hop to the thread pool exactly
    // as they do for a real large report. Even indices are processed, odd are filter-skipped.
    private static List<DryRunFileRow> ManyRows(int count)
    {
        List<DryRunFileRow> rows = new(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new DryRunFileRow(
                @"C:\s", $"file-{i:D6}.txt", @"C:\s",
                i % 2 == 0 ? OperationKind.Processed : OperationKind.SkippedByFilter,
                null,
                i % 2 == 0 ? "KeepSource" : null,
                []));
        }
        return rows;
    }

    [Fact]
    public async Task Rapid_filter_toggles_on_a_large_report_coalesce_to_the_latest_state()
    {
        var tab = new DryRunSourcesTab(TimeSpan.Zero);
        tab.Load(ManyRows(6_000), @"C:\s");
        Assert.Equal(6_000, tab.VisibleRows.Count);

        // Click chips in quick succession; each toggle supersedes the rebuild before it, so only
        // the last filter state may publish.
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = false;
        tab.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        await tab.PendingRebuild;

        Assert.Equal(3_000, tab.VisibleRows.Count);
        Assert.All(tab.VisibleRows, r => Assert.True(r.IsUntouched));
    }

    [Fact]
    public async Task Loading_a_new_report_supersedes_an_in_flight_rebuild()
    {
        var tab = new DryRunSourcesTab(TimeSpan.Zero);
        tab.Load(ManyRows(100_000), @"C:\s");   // large enough that the rebuild is still computing below

        // Kick off a background rebuild, then load a replacement report while it is in flight —
        // the load cancels it, and the superseded rebuild must never publish the old rows.
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        Task superseded = tab.PendingRebuild;
        List<DryRunFileRow> replacement =
        [
            new(@"C:\s", "alpha.txt", @"C:\s", OperationKind.Processed, null, "KeepSource", []),
        ];
        tab.Load(replacement, @"C:\s");
        await superseded;

        DryRunFileRow only = Assert.Single(tab.VisibleRows);
        Assert.Equal("alpha.txt", only.FileName);
    }

    [Fact]
    public async Task Large_rebuilds_flag_IsRebuilding_until_they_publish()
    {
        var tab = new DryRunSourcesTab(TimeSpan.Zero);
        tab.Load(ManyRows(6_000), @"C:\s");
        Assert.False(tab.IsRebuilding);

        // The flag is set synchronously before the rebuild hops to the thread pool, and cleared by
        // the publish — the window the view's loading overlay is visible for.
        tab.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        Assert.True(tab.IsRebuilding);
        await tab.PendingRebuild;
        Assert.False(tab.IsRebuilding);
    }

    [Fact]
    public async Task Small_rebuilds_never_flag_IsRebuilding()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        // Under the sync threshold the rebuild completes in the same dispatcher frame — the
        // loading overlay must never flicker for small reports.
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = true;
        Assert.False(viewModel.Sources.IsRebuilding);
        Assert.Single(viewModel.Sources.VisibleRows);
    }

    [Fact]
    public void Prepare_report_honours_cancellation()
    {
        DryRunReport report = SampleReport(Guid.NewGuid());
        Assert.Throws<OperationCanceledException>(
            () => DryRunViewModel.PrepareReport(report, new CancellationToken(canceled: true)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(condition(), "condition was not met within the timeout");
    }
}
