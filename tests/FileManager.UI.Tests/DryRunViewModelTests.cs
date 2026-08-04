using System.Linq;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
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

    // ── The footer's Mirror warning ─────────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_MIRROR_profile_warns_about_removing_files_from_the_targets()
    {
        var (viewModel, _) = NewViewModel();

        viewModel.ApplySyncMode(SyncMode.AdditiveArchive);
        Assert.Equal("", viewModel.MirrorWarning);

        viewModel.ApplySyncMode(SyncMode.Mirror);
        Assert.Contains("MIRROR", viewModel.MirrorWarning);
        // The filter caveat is load-bearing, not padding: tightening a filter on a Mirror profile removes
        // copies the profile made earlier, which nothing else on screen says.
        Assert.Contains("filters", viewModel.MirrorWarning);
    }

    [Fact]
    public void Closing_the_profile_drops_the_Mirror_warning()
    {
        var (viewModel, _) = NewViewModel();
        viewModel.ApplySyncMode(SyncMode.Mirror);

        viewModel.ClearProfile();

        Assert.Equal("", viewModel.MirrorWarning);
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

    /// <summary>A store holding source files only (no destination operations) — the shape the
    /// tab-level filter/search/rebuild tests need. Rows are no longer objects a test can construct
    /// directly: they are handles into a <see cref="DryRunRowStore"/>, so a test that used to build a
    /// <c>List&lt;DryRunFileRow&gt;</c> builds one of these instead.</summary>
    private DryRunRowStore SourceStore(params (string Name, OperationKind Kind, OnSuccessAction? Disposition)[] files)
    {
        List<DryRunFile> sourceFiles = [];
        List<DryRunOperation> sourceOps = [];
        for (int i = 0; i < files.Length; i++)
        {
            (string name, OperationKind kind, OnSuccessAction? disposition) = files[i];
            sourceFiles.Add(Pf($@"C:\s\{name}", @"C:\s"));
            sourceOps.Add(SrcOp(i, $@"C:\s\{name}", @"C:\s", kind, disposition));
        }
        return DryRunRowStore.FromReport(Report(Guid.NewGuid(), sourceFiles, sourceOps, [], []));
    }

    /// <summary>Enough rows to cross <c>DryRunRebuild.SyncThreshold</c>, so rebuilds hop to the thread
    /// pool exactly as they do for a real large report. Even indices are processed, odd are
    /// filter-skipped.</summary>
    private DryRunRowStore ManyRows(int count)
    {
        var files = new (string, OperationKind, OnSuccessAction?)[count];
        for (int i = 0; i < count; i++)
        {
            files[i] = i % 2 == 0
                ? ($"file-{i:D6}.txt", OperationKind.Processed, OnSuccessAction.KeepSource)
                : ($"file-{i:D6}.txt", OperationKind.SkippedByFilter, (OnSuccessAction?)null);
        }
        return SourceStore(files);
    }

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
    public void CanRun_is_true_for_a_new_profile_with_no_persisted_id()
    {
        var (viewModel, _) = NewViewModel();
        viewModel.SetProfile(null, "New Profile");   // never-saved draft: id is null

        Assert.Null(viewModel.ProfileId);
        Assert.True(viewModel.CanRun);
    }

    [Fact]
    public void ClearProfile_disables_the_run()
    {
        var (viewModel, _) = NewViewModel();
        viewModel.ClearProfile();

        Assert.Null(viewModel.ProfileId);
        Assert.False(viewModel.CanRun);
    }

    // Sending the editor's draft, and refusing on a draft parse error, now belong to the shell command
    // that starts the run — see MainWindowViewModelPreviewTests.

    [Fact]
    public async Task Blast_radius_banner_reflects_the_report()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await RunPlans.PreviewAsync(viewModel, gateway);

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

        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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

        await RunPlans.PreviewAsync(viewModel, gateway);

        var sourceRel = viewModel.Sources.VisibleRows
            .Select(r => System.IO.Path.GetRelativePath(r.SourceRoot!, r.SourcePath)).ToList();
        var destRel = viewModel.Destinations.VisibleRows
            .Select(r => System.IO.Path.GetRelativePath(r.Primary.TargetRoot, r.Primary.TargetPath)).ToList();

        Assert.Equal(sourceRel, destRel);   // identical relative-path ordering in both tabs
    }

    [Fact]
    public async Task Sources_rows_with_duplicate_key_and_root_keep_report_order()
    {
        // Guards the sort's tertiary tiebreak: rows equal on BOTH the relative key ("a.txt") AND the
        // root (C:\src) must keep their original report order — a stable LINQ OrderBy does, a raw
        // (unstable) Array.Sort would not. Indices 1-3 collide on key+root; only their disposition
        // distinguishes them, and it must come out in report order after z.txt (index 0) sorts last.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles:
            [
                Pf(@"C:\src\z.txt", @"C:\src"),   // 0 — distinct key, sorts last
                Pf(@"C:\src\a.txt", @"C:\src"),   // 1 ┐
                Pf(@"C:\src\a.txt", @"C:\src"),   // 2 ├ duplicate key + root
                Pf(@"C:\src\a.txt", @"C:\src"),   // 3 ┘
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\src\z.txt", @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(1, @"C:\src\a.txt", @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(2, @"C:\src\a.txt", @"C:\src", OperationKind.SkippedByFilter, detail: "glob"),
                SrcOp(3, @"C:\src\a.txt", @"C:\src", OperationKind.SkippedUnchanged),
            ],
            destinationFiles: [],
            destinationOps: []);

        await RunPlans.PreviewAsync(viewModel, gateway);

        var order = viewModel.Sources.VisibleRows.Select(r => r.Disposition).ToList();
        Assert.Equal(
            new[]
            {
                OperationKind.Processed,          // a.txt (index 1)
                OperationKind.SkippedByFilter,    // a.txt (index 2)
                OperationKind.SkippedUnchanged,   // a.txt (index 3)
                OperationKind.Processed,          // z.txt (index 0), sorts last
            },
            order);
    }

    [Fact]
    public async Task Destination_rows_with_duplicate_key_and_root_keep_report_order()
    {
        // The Destinations-tab counterpart: two grouped rows collide on the source relative key
        // ("a.txt") and the source root (C:\src); their report order (src 1 before src 2) must survive
        // the sort ahead of z.txt (src 0). Only the destination detail distinguishes the colliding rows.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles:
            [
                Pf(@"C:\src\z.txt", @"C:\src"),   // 0
                Pf(@"C:\src\a.txt", @"C:\src"),   // 1 ┐ duplicate key + root
                Pf(@"C:\src\a.txt", @"C:\src"),   // 2 ┘
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\src\z.txt", @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(1, @"C:\src\a.txt", @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(2, @"C:\src\a.txt", @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource),
            ],
            destinationFiles: [],
            destinationOps:
            [
                DstOp(OperationKind.New, @"D:\dst\z.txt", @"D:\dst", sourceIndex: 0, detail: "z"),
                DstOp(OperationKind.New, @"D:\dst\a.txt", @"D:\dst", sourceIndex: 1, detail: "one"),
                DstOp(OperationKind.New, @"D:\dst\a.txt", @"D:\dst", sourceIndex: 2, detail: "two"),
            ]);

        await RunPlans.PreviewAsync(viewModel, gateway);

        var details = viewModel.Destinations.VisibleRows.Select(r => r.Primary.Detail).ToList();
        Assert.Equal(new[] { "one", "two", "z" }, details);
    }

    [Fact]
    public async Task Large_report_parallel_projection_preserves_stable_ordering()
    {
        // Above DryRunRebuild.SyncThreshold (5,000) PrepareReport projects and sorts on the thread pool
        // (parallel row build, parallel key fill, concurrent tab loads). This drives that path and pins
        // the invariant it must uphold: keys sort ascending and rows equal on key keep report order.
        // Every row's key is just its file name (all under one root); three names give three big tie
        // groups, and each op's detail carries its report index so stable order = strictly increasing.
        const int n = 6_000;
        string[] names = ["a.txt", "b.txt", "c.txt"];
        var sourceFiles = new List<DryRunFile>(n);
        var sourceOps = new List<DryRunOperation>(n);
        var destinationOps = new List<DryRunOperation>(n);
        for (int i = 0; i < n; i++)
        {
            string src = $@"C:\src\{names[i % 3]}";
            string dst = $@"D:\dst\{names[i % 3]}";
            sourceFiles.Add(Pf(src, @"C:\src"));
            sourceOps.Add(SrcOp(i, src, @"C:\src", OperationKind.Processed, OnSuccessAction.KeepSource, detail: i.ToString()));
            destinationOps.Add(DstOp(OperationKind.New, dst, @"D:\dst", sourceIndex: i, detail: i.ToString()));
        }
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value, sourceFiles, sourceOps, [], destinationOps);

        await RunPlans.PreviewAsync(viewModel, gateway);

        AssertStableByName(viewModel.Sources.VisibleRows.Select(r => (r.FileName, r.DecidingFilter!)).ToList());
        AssertStableByName(viewModel.Destinations.VisibleRows.Select(r => (r.FileName, r.Primary.Detail!)).ToList());

        // Keys ascending, and within each equal-key group the report-index marker strictly increases —
        // the tertiary original-index tiebreak the (unstable) Array.Sort depends on.
        static void AssertStableByName(List<(string Name, string Marker)> rows)
        {
            Assert.Equal(n, rows.Count);
            Assert.Equal(rows.Select(r => r.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                rows.Select(r => r.Name).ToList());
            string current = "";
            int last = -1;
            foreach ((string name, string marker) in rows)
            {
                if (name != current) { current = name; last = -1; }
                int index = int.Parse(marker);
                Assert.True(index > last, $"stable order violated within '{name}': {index} after {last}");
                last = index;
            }
        }
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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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

        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.False(viewModel.Sources.ShowSourceFacet);
        Assert.Empty(viewModel.Sources.SourceFacets);
    }

    [Fact]
    public async Task Sources_search_matches_source_and_target_paths()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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

    [Fact]
    public async Task Tree_collapses_top_level_when_first_expansion_reveals_too_many_rows()
    {
        // One top-level folder holding more direct files than the limit → expanding it would flood the
        // view, so the tree opens collapsed (chevron shown, nothing expanded).
        const int n = DryRunTreeNode.AutoExpandChildLimit + 1;
        var sourceFiles = new List<DryRunFile>(n);
        var sourceOps = new List<DryRunOperation>(n);
        for (int i = 0; i < n; i++)
        {
            string path = $@"C:\r\big\file-{i}.txt";
            sourceFiles.Add(Pf(path, @"C:\r"));
            sourceOps.Add(SrcOp(i, path, @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource));
        }
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value, sourceFiles, sourceOps, [], []);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        await viewModel.Sources.PendingRebuild;

        DryRunTreeNode top = Assert.Single(viewModel.Sources.Tree);
        Assert.Equal("big", top.Name);
        Assert.True(top.HasChildren);
        Assert.False(top.IsExpanded);
    }

    [Fact]
    public async Task Tree_stays_expanded_when_many_rows_collapse_to_few_distinct_names()
    {
        // One top-level folder holding more direct rows than the limit, but only 3 distinct file names.
        // Rows sharing a name collapse to one leaf on expand, so the first expansion reveals just 3 rows —
        // the heuristic counts distinct names, not raw rows, so the tree still auto-expands.
        const int n = DryRunTreeNode.AutoExpandChildLimit + 1;
        var sourceFiles = new List<DryRunFile>(n);
        var sourceOps = new List<DryRunOperation>(n);
        for (int i = 0; i < n; i++)
        {
            string path = $@"C:\r\big\file-{i % 3}.txt";   // only 3 distinct names in the folder
            sourceFiles.Add(Pf(path, @"C:\r"));
            sourceOps.Add(SrcOp(i, path, @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource));
        }
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value, sourceFiles, sourceOps, [], []);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        await viewModel.Sources.PendingRebuild;

        DryRunTreeNode top = Assert.Single(viewModel.Sources.Tree);
        Assert.Equal("big", top.Name);
        Assert.True(top.HasChildren);
        Assert.True(top.IsExpanded);   // 3 distinct names ≤ limit → auto-expands despite the row count
    }

    [Fact]
    public async Task Tree_stays_expanded_for_a_deep_tree_with_few_top_level_children()
    {
        // A single top-level folder with only 3 immediate subfolders — deep, and well over the limit in
        // TOTAL files, but only 3 top-level children. The threshold counts the top level's own children,
        // not the whole subtree, so it still auto-expands; the deeper subfolders stay collapsed.
        const int n = DryRunTreeNode.AutoExpandChildLimit + 50;   // total files > limit
        var sourceFiles = new List<DryRunFile>(n);
        var sourceOps = new List<DryRunOperation>(n);
        for (int i = 0; i < n; i++)
        {
            string path = $@"C:\r\a\s{i % 3}\file-{i}.txt";
            sourceFiles.Add(Pf(path, @"C:\r"));
            sourceOps.Add(SrcOp(i, path, @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource));
        }
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value, sourceFiles, sourceOps, [], []);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        await viewModel.Sources.PendingRebuild;

        DryRunTreeNode top = Assert.Single(viewModel.Sources.Tree);
        Assert.Equal("a", top.Name);
        Assert.True(top.IsExpanded);   // only 3 top-level children → auto-expands despite the file count

        // The three subfolders are revealed but themselves collapsed — depth doesn't auto-expand.
        var subfolders = top.Children.Where(c => c.IsDirectory).ToList();
        Assert.Equal(3, subfolders.Count);
        Assert.All(subfolders, s => Assert.False(s.IsExpanded));
    }

    [Fact]
    public async Task Tree_materializes_file_leaves_lazily_on_expand()
    {
        // The forest is built with directory nodes + rolled-up pills only; a directory's file leaves are
        // not created until its Children are read (the user expands it). This keeps ~1 node per file off
        // the heap for collapsed subtrees.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);
        viewModel.Sources.ShowTree = true;

        DryRunTreeNode sub = Assert.Single(viewModel.Sources.Tree);
        Assert.True(sub.HasChildren);          // chevron shows without the leaves existing
        Assert.True(sub.LeavesPending);        // …and they do not exist yet
        Assert.Contains(sub.Pills, p => p.Tip == "processed" && p.CountText == "2");   // dir totals are ready

        IReadOnlyList<DryRunTreeNode> children = sub.Children;   // expanding materializes the leaves
        Assert.False(sub.LeavesPending);
        Assert.Equal(new[] { "one.txt", "three.txt", "two.tmp" }, children.Select(c => c.Name));  // files, alpha
        Assert.All(children, c => Assert.False(c.IsDirectory));
        Assert.Same(children, sub.Children);   // stable reference — the grid won't rebuild child rows
    }

    [Fact]
    public async Task Folder_context_menu_commands_drive_expansion_through_the_source()
    {
        // A single top-level folder "a" with three subfolders, each holding a file: "a" auto-expands
        // (few top-level children) while the subfolders start collapsed. Exercises the folder
        // right-click commands, which the view binds from the tree cell's context menu.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles:
            [
                Pf(@"C:\r\a\s0\f0.txt", @"C:\r"),
                Pf(@"C:\r\a\s1\f1.txt", @"C:\r"),
                Pf(@"C:\r\a\s2\f2.txt", @"C:\r"),
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\r\a\s0\f0.txt", @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(1, @"C:\r\a\s1\f1.txt", @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(2, @"C:\r\a\s2\f2.txt", @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource),
            ],
            destinationFiles: [], destinationOps: []);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        await viewModel.Sources.PendingRebuild;
        Assert.NotNull(viewModel.Sources.TreeSource);   // BuildSource attached the source to the controller

        DryRunTreeNode top = Assert.Single(viewModel.Sources.Tree);
        var subfolders = top.Children.Where(c => c.IsDirectory).ToList();
        Assert.Equal(3, subfolders.Count);
        Assert.True(top.IsExpanded);                           // auto-expanded top level
        Assert.All(subfolders, s => Assert.False(s.IsExpanded));

        // Open Folder and All Nested Folders → the whole subtree under "a" expands (source API,
        // viewport-independent — the subfolders were never realized).
        top.OpenFolderRecursiveCommand.Execute(null);
        Assert.True(top.IsExpanded);
        Assert.All(subfolders, s => Assert.True(s.IsExpanded));

        // Close All Folders → every folder in the tree collapses.
        top.CloseAllCommand.Execute(null);
        Assert.False(top.IsExpanded);
        Assert.All(subfolders, s => Assert.False(s.IsExpanded));

        // Expand All Folders → every folder in the tree expands.
        top.ExpandAllCommand.Execute(null);
        Assert.True(top.IsExpanded);
        Assert.All(subfolders, s => Assert.True(s.IsExpanded));

        // Single Open/Close affect only the target folder, not its siblings.
        DryRunTreeNode one = subfolders[0];
        one.CloseFolderCommand.Execute(null);
        Assert.False(one.IsExpanded);
        Assert.True(subfolders[1].IsExpanded);
        one.OpenFolderCommand.Execute(null);
        Assert.True(one.IsExpanded);
    }

    [Fact]
    public async Task Toggle_expand_collapse_all_flips_the_whole_subtree_by_current_state()
    {
        // Same shape as above: "a" auto-expands, its three subfolders start collapsed. The Ctrl+Enter
        // keyboard toggle (ToggleExpandCollapseAll) expands the whole subtree when the folder is
        // collapsed and collapses it when open — driving off the folder's own IsExpanded.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles:
            [
                Pf(@"C:\r\a\s0\f0.txt", @"C:\r"),
                Pf(@"C:\r\a\s1\f1.txt", @"C:\r"),
                Pf(@"C:\r\a\s2\f2.txt", @"C:\r"),
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\r\a\s0\f0.txt", @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(1, @"C:\r\a\s1\f1.txt", @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(2, @"C:\r\a\s2\f2.txt", @"C:\r", OperationKind.Processed, OnSuccessAction.KeepSource),
            ],
            destinationFiles: [], destinationOps: []);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        await viewModel.Sources.PendingRebuild;

        DryRunTreeNode top = Assert.Single(viewModel.Sources.Tree);
        var subfolders = top.Children.Where(c => c.IsDirectory).ToList();
        Assert.True(top.IsExpanded);   // auto-expanded, subfolders collapsed

        // Open → toggle collapses the whole subtree (the folder itself and every descendant).
        top.ToggleExpandCollapseAllCommand.Execute(null);
        Assert.False(top.IsExpanded);
        Assert.All(subfolders, s => Assert.False(s.IsExpanded));

        // Collapsed → toggle expands the whole subtree.
        top.ToggleExpandCollapseAllCommand.Execute(null);
        Assert.True(top.IsExpanded);
        Assert.All(subfolders, s => Assert.True(s.IsExpanded));

        // Regression: after expand-all then collapse-all, re-opening ONLY the top folder must show the
        // subfolders collapsed — the collapse must reset the descendants, not leave them flagged open.
        top.ToggleExpandCollapseAllCommand.Execute(null);   // collapse the whole subtree again
        Assert.False(top.IsExpanded);
        Assert.All(subfolders, s => Assert.False(s.IsExpanded));

        top.OpenFolderCommand.Execute(null);                 // single open of just the top folder
        Assert.True(top.IsExpanded);
        Assert.All(subfolders, s => Assert.False(s.IsExpanded));   // subfolders stay collapsed
    }

    [Fact]
    public async Task Lazy_leaves_merge_duplicate_names_with_summed_counts()
    {
        // Two rows at the same resulting path collapse to one leaf whose counts sum — the same dedup the
        // eager build did inline, now reproduced when leaves are built on expand.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles:
            [
                Pf(@"C:\p\sub\dup.txt", @"C:\p"),
                Pf(@"C:\p\sub\dup.txt", @"C:\p"),
                Pf(@"C:\p\sub\z.txt", @"C:\p"),
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\p\sub\dup.txt", @"C:\p", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(1, @"C:\p\sub\dup.txt", @"C:\p", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(2, @"C:\p\sub\z.txt", @"C:\p", OperationKind.Processed, OnSuccessAction.KeepSource),
            ],
            destinationFiles: [], destinationOps: []);
        await RunPlans.PreviewAsync(viewModel, gateway);
        viewModel.Sources.ShowTree = true;

        DryRunTreeNode sub = Assert.Single(viewModel.Sources.Tree);
        var children = sub.Children;
        Assert.Equal(new[] { "dup.txt", "z.txt" }, children.Select(c => c.Name));   // dup merged into one leaf
        DryRunTreeNode dup = children.Single(c => c.Name == "dup.txt");
        Assert.Contains(dup.Pills, p => p.Tip == "processed" && p.CountText == "2");
    }

    [Fact]
    public async Task Snapshotting_expansion_does_not_materialize_leaves()
    {
        // CollectExpanded runs on every filter keystroke to preserve expand state. It must walk the
        // internal subdirectory structure, never force lazy leaf materialization.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = Report(viewModel.ProfileId!.Value,
            sourceFiles:
            [
                Pf(@"C:\p\sub\top.txt", @"C:\p"),
                Pf(@"C:\p\sub\deep\a.txt", @"C:\p"),
                Pf(@"C:\p\sub\deep\b.txt", @"C:\p"),
            ],
            sourceOps:
            [
                SrcOp(0, @"C:\p\sub\top.txt", @"C:\p", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(1, @"C:\p\sub\deep\a.txt", @"C:\p", OperationKind.Processed, OnSuccessAction.KeepSource),
                SrcOp(2, @"C:\p\sub\deep\b.txt", @"C:\p", OperationKind.Processed, OnSuccessAction.KeepSource),
            ],
            destinationFiles: [], destinationOps: []);
        await RunPlans.PreviewAsync(viewModel, gateway);
        viewModel.Sources.ShowTree = true;

        DryRunTreeNode sub = Assert.Single(viewModel.Sources.Tree);
        Assert.True(sub.LeavesPending);   // sub's direct file (top.txt) is deferred

        DryRunTreeNode.CollectExpanded(viewModel.Sources.Tree);   // what a filter keystroke snapshots

        Assert.True(sub.LeavesPending);   // the snapshot did not build any leaves
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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

        DryRunFileRow row = viewModel.Sources.VisibleRows.First();
        Assert.Equal(@"sub\", row.ParentDisplay);
        Assert.Equal("one.txt", row.FileName);
    }

    [Fact]
    public async Task Report_defaults_to_list_view_in_both_tabs()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        Assert.NotEmpty(viewModel.Sources.Tree);
        Assert.NotNull(viewModel.Sources.TreeSource);

        // A second run applies a fresh report without the user toggling the tree off first.
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.False(viewModel.Sources.ShowTree);
        Assert.Empty(viewModel.Sources.Tree);
        Assert.Null(viewModel.Sources.TreeSource);
    }

    [Fact]
    public async Task Starting_a_new_run_releases_the_previous_preview_before_building_the_next()
    {
        // Change 7: a re-run must release the prior report's rows at run start, not hold them alive
        // until the next report is built — otherwise consecutive runs peak at ~2x the row footprint.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);
        Assert.True(viewModel.HasReport);
        Assert.NotEmpty(viewModel.Sources.VisibleRows);

        // Gate the second preview so it stays parked in the plan stream, after BeginPlanning's
        // synchronous clear.
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        gateway.RunPlanGate = new TaskCompletionSource();
        Task running = RunPlans.PreviewAsync(viewModel, gateway);

        // The previous preview is already gone — released synchronously before the gateway await, well
        // before the new report exists to replace it.
        Assert.False(viewModel.HasReport);
        Assert.Empty(viewModel.Sources.VisibleRows);
        Assert.Empty(viewModel.Destinations.VisibleRows);

        gateway.RunPlanGate.SetResult();
        await running;
        Assert.True(viewModel.HasReport);   // the new report applies normally
        Assert.NotEmpty(viewModel.Sources.VisibleRows);
    }

    [Fact]
    public async Task Starting_a_preview_does_not_trigger_the_memory_trim_when_a_report_is_already_loaded()
    {
        // Regression for the UI-thread freeze: the clear at the start of a preview used to inherit
        // UiMemoryTrim's blocking two-pass GC.Collect whenever a report was already showing, so every
        // re-preview froze the window before the scan even started. BeginPlanning must never invoke it —
        // only ReportClosed() (profile select/deselect) may.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);
        Assert.True(viewModel.HasReport);

        Action original = UiMemoryTrim.AfterPreviewClosedHook;
        int callCount = 0;
        UiMemoryTrim.AfterPreviewClosedHook = () => callCount++;
        try
        {
            gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
            await RunPlans.PreviewAsync(viewModel, gateway);
        }
        finally
        {
            UiMemoryTrim.AfterPreviewClosedHook = original;
        }

        Assert.Equal(0, callCount);
        Assert.True(viewModel.HasReport);   // the re-run still applied normally
    }

    [Fact]
    public async Task Re_running_after_a_tree_toggle_replaces_the_grid_source()
    {
        // Regression for the tree-view memory leak: OnTreeChanged now disposes the previous
        // HierarchicalTreeDataGridSource when the forest is replaced (its realized row cache and
        // per-node IsExpanded subscriptions otherwise strand the whole old forest, so each toggle +
        // re-run stacked another forest on the heap). Headless there's no control to root the leak, so
        // this pins the observable contract and exercises the realize → re-run → dispose path: no throw
        // from the dispose ordering, and the previous source is gone after the next run.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.Sources.ShowTree = true;
        await viewModel.Sources.PendingRebuild;
        var previous = viewModel.Sources.TreeSource;
        Assert.NotNull(previous);
        _ = previous.Rows.Count;   // realize the row cache the leak used to strand

        gateway.DryRunResult = NestedSourcesReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.Null(viewModel.Sources.TreeSource);   // list-view default; the prior source was released
    }

    [Fact]
    public async Task Truncated_report_surfaces_a_notice_mentioning_deletions_are_hidden()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with { Truncated = true };

        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.True(viewModel.WasTruncated);
        Assert.Contains("truncated", viewModel.TruncationNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deletions", viewModel.TruncationNotice, StringComparison.OrdinalIgnoreCase);

        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);
        Assert.False(viewModel.WasTruncated);
        Assert.Equal("", viewModel.TruncationNotice);
    }

    [Fact]
    public async Task Gateway_error_surfaces_as_a_banner()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new IpcError("DRY_RUN_FAILED", "scan failed: boom");

        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.False(viewModel.HasReport);
        Assert.Contains("boom", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Transport_errors_point_at_the_service_log()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new IpcError("IPC_TRANSPORT", "connection closed");

        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.False(viewModel.HasReport);
        Assert.Contains("connection closed", viewModel.ErrorMessage);
        Assert.Contains(@"FileManager\logs", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Unexpected_gateway_exception_surfaces_as_a_banner_not_a_fault()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.RunPlanException = new InvalidOperationException("wire format drifted");

        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.False(viewModel.HasReport);
        Assert.Contains("wire format drifted", viewModel.ErrorMessage);
        // And no footer: a plan that could not be displayed must never be approvable.
        Assert.Null(viewModel.PendingRunId);
    }

    [Fact]
    public async Task A_preview_streams_the_plan_of_the_run_it_was_given()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);

        // The rows come from the RUN's frozen snapshot, not from a fresh simulation — that is the whole
        // point, and it is why the gateway has no dry-run method left to reach for.
        Assert.Equal(runId, Assert.Single(gateway.RunPlanStreamCalls));
    }

    [Fact]
    public async Task Run_status_shows_phases_and_clears_when_the_preview_completes()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        List<string> observed = [];
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DryRunViewModel.RunStatusText))
                observed.Add(viewModel.RunStatusText);
        };

        await RunPlans.PreviewAsync(viewModel, gateway);

        // The phase transitions: the planning caption from the moment Preview is pressed, the building
        // caption once the plan has streamed, and empty once the rows are up.
        Assert.Equal("Working out what this will do…", observed.First());
        Assert.Contains("Building the lists…", observed);
        Assert.Equal("", viewModel.RunStatusText);
        Assert.False(viewModel.IsPreviewing);
    }

    [Fact]
    public async Task IsPreviewing_spans_the_whole_wait_and_clears_at_the_end()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        gateway.RunPlanGate = new TaskCompletionSource();

        Task running = RunPlans.PreviewAsync(viewModel, gateway);
        // Set before the plan stream is even awaited: the planning phase is a real wait the user must see
        // a progress bar for, and it reports nothing to this view model.
        Assert.True(viewModel.IsPreviewing);

        gateway.RunPlanGate.SetResult();
        await running;

        Assert.False(viewModel.IsPreviewing);
    }

    // ── The shape a REAL plan replay has ────────────────────────────────────────────────────────

    /// <summary>What <c>get-run-plan-stream</c> actually emits for an additive profile: one source row
    /// per planned copy, each with its New destination, and — because nothing is being removed — not one
    /// orphan.
    /// <para>Distinct from <see cref="SampleReport"/> on purpose. Every other case here scripts a rich
    /// dry-run report, which is the readable way to describe rows but is NOT what a plan replay looks
    /// like; the empty-panel defect lived exactly in that gap.</para></summary>
    private DryRunReport AdditivePlanReplay(int files = 2)
    {
        List<DryRunFile> sourceFiles = [];
        List<DryRunOperation> sourceOps = [];
        List<DryRunOperation> destinationOps = [];
        for (int i = 0; i < files; i++)
        {
            sourceFiles.Add(Pf($@"C:\s\file-{i}.txt", @"C:\s"));
            sourceOps.Add(SrcOp(i, $@"C:\s\file-{i}.txt", @"C:\s",
                OperationKind.Processed, OnSuccessAction.KeepSource));
            destinationOps.Add(DstOp(OperationKind.New, $@"D:\d\file-{i}.txt", @"D:\d", sourceIndex: i));
        }
        return Report(Guid.NewGuid(), sourceFiles, sourceOps, [], destinationOps);
    }

    [Fact]
    public async Task An_additive_plan_fills_BOTH_panels()
    {
        // The reported symptom at view-model level: an additive profile removes nothing, so if a replay
        // carries only orphans on the destination side this panel is empty and the user cannot tell that
        // from "the preview found nothing".
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = AdditivePlanReplay();

        await RunPlans.PreviewAsync(viewModel, gateway, copies: 2);

        Assert.True(viewModel.HasReport);
        Assert.Equal(2, viewModel.Sources.VisibleRows.Count);
        Assert.Equal(2, viewModel.Destinations.VisibleRows.Count);
    }

    [Fact]
    public async Task Every_source_row_in_a_plan_replay_lists_where_its_content_goes()
    {
        // A source row whose Targets are empty renders as a file the run will read and no statement about
        // what it does with it.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = AdditivePlanReplay(files: 1);

        await RunPlans.PreviewAsync(viewModel, gateway, copies: 1);

        DryRunFileRow row = Assert.Single(viewModel.Sources.VisibleRows);
        Assert.Equal(@"D:\d\file-0.txt", Assert.Single(row.Targets).Path);
    }

    // ── The approval footer ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_footer_appears_with_the_rows_and_states_the_plans_totals()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway, copies: 7, deletes: 2);

        Assert.Equal(runId, viewModel.PendingRunId);
        Assert.Equal(7, viewModel.PlannedCopies);
        Assert.Equal(2, viewModel.PlannedDeletes);
        // Deletions lead: removing files from a target is the only part that feels irreversible.
        Assert.StartsWith("2 file(s) to REMOVE", viewModel.PlanSummary);
        Assert.Contains("7 file(s) to copy or update", viewModel.PlanSummary);
    }

    [Fact]
    public async Task With_nothing_to_remove_the_summary_is_only_about_copies()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await RunPlans.PreviewAsync(viewModel, gateway, copies: 3);

        Assert.StartsWith("3 file(s) to copy or update", viewModel.PlanSummary);
        Assert.DoesNotContain("REMOVE", viewModel.PlanSummary);
    }

    [Fact]
    public async Task A_TRUNCATED_plan_says_nothing_will_be_removed()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await RunPlans.PreviewAsync(viewModel, gateway, copies: 5, deletes: 4, truncated: true);

        Assert.True(viewModel.PlanTruncated);
        // Not a caveat on the numbers — the deletion pass refuses a truncated plan outright, so a footer
        // that still promised removals would be lying.
        Assert.Contains("No files will be removed", viewModel.PlanTruncationNotice);
    }

    [Fact]
    public async Task Approving_starts_the_run_that_was_previewed_and_retires_the_footer()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        bool? answered = null;
        viewModel.RunAnswered = approve => answered = approve;

        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);
        await viewModel.ApproveRunCommand.ExecuteAsync(null);

        Assert.Equal((runId, true), Assert.Single(gateway.ApproveRunCalls));
        Assert.True(answered);
        Assert.Null(viewModel.PendingRunId);
        // The rows stay: the user is now watching the run they just approved, against the list of what it
        // will do.
        Assert.True(viewModel.HasReport);
    }

    [Fact]
    public async Task Discarding_declines_the_run_and_changes_nothing()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        bool? answered = null;
        viewModel.RunAnswered = approve => answered = approve;

        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);
        await viewModel.DeclineRunCommand.ExecuteAsync(null);

        Assert.Equal((runId, false), Assert.Single(gateway.ApproveRunCalls));
        Assert.False(answered);
        Assert.Null(viewModel.PendingRunId);
    }

    [Fact]
    public async Task A_second_press_of_Approve_cannot_answer_the_same_run_twice()
    {
        // The engine refuses a second approve with RUN_NOT_APPROVABLE, so without clearing the pending id
        // FIRST a double-click would put that refusal in the error banner for no reason.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        await viewModel.ApproveRunCommand.ExecuteAsync(null);
        await viewModel.ApproveRunCommand.ExecuteAsync(null);

        Assert.Single(gateway.ApproveRunCalls);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task A_refused_approval_lands_in_the_error_banner()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);
        gateway.ApproveRunResult = new IpcError("RUN_NOT_APPROVABLE", "run is Closed, not awaiting approval");

        await viewModel.ApproveRunCommand.ExecuteAsync(null);

        Assert.Contains("Could not start the run", viewModel.ErrorMessage);
        Assert.Contains("not awaiting approval", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task A_superseding_preview_declines_the_run_the_previous_one_left_parked()
    {
        // A run in AwaitingApproval holds a snapshot directory and has no expiry, so an unanswered
        // preview would leak one for the lifetime of the service.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        Guid first = await RunPlans.PreviewAsync(viewModel, gateway);

        Guid second = await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.Equal((first, false), Assert.Single(gateway.ApproveRunCalls));
        Assert.Equal(second, viewModel.PendingRunId);
    }

    [Fact]
    public async Task Closing_the_profile_declines_the_run_the_preview_left_parked()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.ClearProfile();

        Assert.Equal((runId, false), Assert.Single(gateway.ApproveRunCalls));
        Assert.Null(viewModel.PendingRunId);
    }

    [Fact]
    public async Task A_failed_plan_stream_leaves_no_footer_to_approve()
    {
        var (viewModel, gateway) = NewViewModel();
        Guid runId = Guid.NewGuid();
        gateway.RunPlanResults[runId] = new IpcError("RUN_NOT_FOUND", $"no run with id {runId}");

        viewModel.BeginPlanning();
        await viewModel.LoadPlanAsync(RunPlans.Planned(runId, viewModel.ProfileId!.Value));

        Assert.Contains("Preview failed", viewModel.ErrorMessage);
        Assert.Null(viewModel.PendingRunId);
        Assert.False(viewModel.HasReport);
    }

    [Fact]
    public async Task Cancellation_resets_to_a_calm_state()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.RunPlanGate = new TaskCompletionSource();
        using CancellationTokenSource cts = new();

        viewModel.BeginPlanning();
        Task running = viewModel.LoadPlanAsync(
            RunPlans.Planned(Guid.NewGuid(), viewModel.ProfileId!.Value), cts.Token);
        cts.Cancel();
        await running;

        Assert.False(viewModel.HasReport);
        Assert.Contains("cancelled", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        // No footer: there is nothing to approve, and offering to would approve a plan never displayed.
        Assert.Null(viewModel.PendingRunId);
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

        await RunPlans.PreviewAsync(viewModel, gateway);

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

        await RunPlans.PreviewAsync(viewModel, gateway);

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

        // No deferred reclaim in this projection, so no "freed at end" figure and no remedy offered.
        Assert.False(d.HasDeferredReclaim);
        Assert.DoesNotContain("proactively", d.WarningText);
    }

    [Fact]
    public async Task A_tight_drive_whose_peak_holds_doomed_mirror_files_is_told_how_to_reclaim_them()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with
        {
            Space = new SpaceProjection
            {
                TotalBytesWritten = 5000,
                TotalNetChangeBytes = 400,
                SafetyMarginBytes = 100,
                Volumes =
                [
                    // Peak (950) crosses capacity − margin (900), and 300 of it is orphans that a
                    // delete-after-copy Mirror keeps on disk until the run ends.
                    new VolumeSpaceEstimate
                    {
                        VolumeRoot = "D:", CapacityKnown = true, TotalCapacityBytes = 1000, UsedNowBytes = 500,
                        FreeNowBytes = 500, ClusterBytes = 1, BytesWrittenBytes = 5000, NetChangeBytes = 400,
                        SettledUsedBytes = 900, DeferredReclaimBytes = 500, MirrorDeferredReclaimBytes = 300,
                        RealisticPeakUsedBytes = 950, SafeCeilingUsedBytes = 980,
                    },
                ],
            },
        };

        await RunPlans.PreviewAsync(viewModel, gateway);

        VolumeSpaceRow d = Assert.Single(viewModel.Space!.Volumes);
        Assert.True(d.HasDeferredReclaim);
        Assert.Equal(ByteSize.Format(500), d.DeferredReclaimText);          // the whole deferred total
        Assert.Contains(ByteSize.Format(300), d.WarningText);               // only the actionable share
        Assert.Contains("proactively", d.WarningText);
    }

    [Fact]
    public async Task Deferred_space_with_no_mirror_share_is_reported_without_offering_a_remedy()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with
        {
            Space = new SpaceProjection
            {
                TotalBytesWritten = 5000,
                TotalNetChangeBytes = 400,
                SafetyMarginBytes = 100,
                Volumes =
                [
                    // All the deferred space is sources awaiting disposal — nothing the user can
                    // retime, so naming a Mirror remedy would be advice they cannot act on.
                    new VolumeSpaceEstimate
                    {
                        VolumeRoot = "D:", CapacityKnown = true, TotalCapacityBytes = 1000, UsedNowBytes = 500,
                        FreeNowBytes = 500, ClusterBytes = 1, BytesWrittenBytes = 5000, NetChangeBytes = 400,
                        SettledUsedBytes = 900, DeferredReclaimBytes = 500, MirrorDeferredReclaimBytes = 0,
                        RealisticPeakUsedBytes = 950, SafeCeilingUsedBytes = 980,
                    },
                ],
            },
        };

        await RunPlans.PreviewAsync(viewModel, gateway);

        VolumeSpaceRow d = Assert.Single(viewModel.Space!.Volumes);
        Assert.True(d.IsDanger);
        Assert.True(d.HasDeferredReclaim);
        Assert.DoesNotContain("proactively", d.WarningText);
    }

    [Fact]
    public async Task Space_is_null_when_the_report_carries_no_projection()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);   // Space defaults null
        await RunPlans.PreviewAsync(viewModel, gateway);
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

        await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.True(viewModel.HasReport);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(2, viewModel.Sources.VisibleRows.Count);
    }

    [Fact]
    public async Task Tree_expansion_persists_across_a_search_driven_rebuild()
    {
        var (viewModel, gateway) = NewViewModel();   // searchDebounce == Zero → rebuilds are synchronous
        gateway.DryRunResult = DeepSourcesReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

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
        tab.Load(SourceStore(
            ("alpha.txt", OperationKind.Processed, OnSuccessAction.KeepSource),
            ("beta.txt", OperationKind.Processed, OnSuccessAction.KeepSource),
            ("gamma.txt", OperationKind.Processed, OnSuccessAction.KeepSource)));
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

    [Fact]
    public async Task Rapid_filter_toggles_on_a_large_report_coalesce_to_the_latest_state()
    {
        var tab = new DryRunSourcesTab(TimeSpan.Zero);
        tab.Load(ManyRows(6_000));
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
        tab.Load(ManyRows(100_000));   // large enough that the rebuild is still computing below

        // Kick off a background rebuild, then load a replacement report while it is in flight —
        // the load cancels it, and the superseded rebuild must never publish the old rows.
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        Task superseded = tab.PendingRebuild;
        tab.Load(SourceStore(("alpha.txt", OperationKind.Processed, OnSuccessAction.KeepSource)));
        await superseded;

        DryRunFileRow only = Assert.Single(tab.VisibleRows);
        Assert.Equal("alpha.txt", only.FileName);
    }

    [Fact]
    public async Task Large_rebuilds_flag_IsRebuilding_until_they_publish()
    {
        var tab = new DryRunSourcesTab(TimeSpan.Zero);
        tab.Load(ManyRows(6_000));
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
        await RunPlans.PreviewAsync(viewModel, gateway);

        // Under the sync threshold the rebuild completes in the same dispatcher frame — the
        // loading overlay must never flicker for small reports.
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = true;
        Assert.False(viewModel.Sources.IsRebuilding);
        Assert.Single(viewModel.Sources.VisibleRows);
    }

    [Fact]
    public void Prepare_report_honours_cancellation()
    {
        DryRunRowStore store = DryRunRowStore.FromReport(SampleReport(Guid.NewGuid()));
        DryRunCompletion completion = new(DateTimeOffset.UnixEpoch, Truncated: false, Space: null);
        Assert.Throws<OperationCanceledException>(
            () => DryRunViewModel.PrepareReport(store, completion, new CancellationToken(canceled: true)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(condition(), "condition was not met within the timeout");
    }
}
