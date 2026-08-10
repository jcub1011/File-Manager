using System.Linq;
using FileManager.Contracts.DryRun;
using Microsoft.Extensions.Time.Testing;
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
    private static (DryRunViewModel ViewModel, FakeIpcGateway Gateway) NewViewModel(TimeProvider? time = null)
    {
        FakeIpcGateway gateway = new();
        // Zero debounce keeps search-driven rebuilds synchronous so tests can assert immediately.
        DryRunViewModel viewModel = new(gateway, searchDebounce: TimeSpan.Zero, time: time);
        viewModel.SetProfile(Guid.NewGuid(), "P");
        return (viewModel, gateway);
    }

    // ── The footer's Mirror warning ─────────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_MIRROR_profile_warns_about_removing_files_from_the_targets()
    {
        var (viewModel, _) = NewViewModel();

        viewModel.ApplyPolicies(SyncMode.AdditiveArchive, OnSuccessAction.KeepSource, null);
        Assert.Equal("", viewModel.MirrorWarning);

        viewModel.ApplyPolicies(SyncMode.Mirror, OnSuccessAction.KeepSource, null);
        Assert.Contains("MIRROR", viewModel.MirrorWarning);
        // The filter caveat is load-bearing, not padding: tightening a filter on a Mirror profile removes
        // copies the profile made earlier, which nothing else on screen says.
        Assert.Contains("filters", viewModel.MirrorWarning);
    }

    [Fact]
    public void Closing_the_profile_drops_the_Mirror_warning()
    {
        var (viewModel, _) = NewViewModel();
        viewModel.ApplyPolicies(SyncMode.Mirror, OnSuccessAction.KeepSource, null);

        viewModel.ClearProfile();

        Assert.Equal("", viewModel.MirrorWarning);
    }

    // ── The footer's source-disposition warning ─────────────────────────────────────────────────

    /// <summary>The modal confirmation this footer replaced was the only place the source disposition was
    /// ever stated ("each source file will then be PERMANENTLY DELETED"), and its test was deleted with
    /// it. Without this, a user reads "1,204 file(s) to copy or update" and loses all 1,204 originals with
    /// nothing on screen having said so.</summary>
    [Theory]
    [InlineData(OnSuccessAction.PermanentDelete, "PERMANENTLY DELETED")]
    [InlineData(OnSuccessAction.MoveToTrash, "RECYCLE BIN")]
    public void A_destructive_disposition_is_stated_and_flagged_as_destructive(
        OnSuccessAction disposition, string expected)
    {
        var (viewModel, _) = NewViewModel();

        viewModel.ApplyPolicies(SyncMode.AdditiveArchive, disposition, null);

        Assert.Contains(expected, viewModel.SourceDispositionWarning, StringComparison.Ordinal);
        Assert.True(viewModel.SourceDispositionDestroys);
        // The danger bar carries it, so the plain-text note must not also render it.
        Assert.False(viewModel.ShowSourceDispositionNote);
    }

    [Fact]
    public void Archiving_the_sources_is_stated_WITHOUT_the_danger_styling()
    {
        var (viewModel, _) = NewViewModel();

        viewModel.ApplyPolicies(SyncMode.AdditiveArchive, OnSuccessAction.MoveToArchive, @"D:\archive");

        // The file is relocated, not lost — worth saying, not worth shouting. The folder is named because
        // "moved to the archive folder" without one is not a statement of where anything went.
        Assert.Contains(@"D:\archive", viewModel.SourceDispositionWarning, StringComparison.Ordinal);
        Assert.False(viewModel.SourceDispositionDestroys);
        Assert.True(viewModel.ShowSourceDispositionNote);
    }

    [Fact]
    public void Keeping_the_sources_says_nothing_at_all()
    {
        var (viewModel, _) = NewViewModel();
        viewModel.ApplyPolicies(SyncMode.AdditiveArchive, OnSuccessAction.PermanentDelete, null);

        viewModel.ApplyPolicies(SyncMode.AdditiveArchive, OnSuccessAction.KeepSource, null);

        // The safe default. Stating "kept in place" beside the counts is noise that dilutes the warnings
        // that matter — and this also pins that switching BACK clears the destructive text.
        Assert.Equal("", viewModel.SourceDispositionWarning);
        Assert.False(viewModel.SourceDispositionDestroys);
        Assert.False(viewModel.ShowSourceDispositionNote);
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

    /// <summary>A plan of source files only (no destination operations) — the shape the filter, search
    /// and rebuild-lifecycle tests need.</summary>
    private DryRunReport SourceReport(params (string Name, OperationKind Kind, OnSuccessAction? Disposition)[] files)
    {
        List<DryRunFile> sourceFiles = [];
        List<DryRunOperation> sourceOps = [];
        for (int i = 0; i < files.Length; i++)
        {
            (string name, OperationKind kind, OnSuccessAction? disposition) = files[i];
            sourceFiles.Add(Pf($@"C:\s\{name}", @"C:\s"));
            sourceOps.Add(SrcOp(i, $@"C:\s\{name}", @"C:\s", kind, disposition));
        }
        return Report(Guid.NewGuid(), sourceFiles, sourceOps, [], []);
    }

    /// <summary>Enough rows to cross <c>DryRunRebuild.SyncThreshold</c>, so a filter change is treated as
    /// slow enough to explain — the same distinction a real large plan draws. Even indices are processed,
    /// odd are filter-skipped.</summary>
    private DryRunReport ManyRowsReport(int count)
    {
        var files = new (string, OperationKind, OnSuccessAction?)[count];
        for (int i = 0; i < count; i++)
        {
            files[i] = i % 2 == 0
                ? ($"file-{i:D6}.txt", OperationKind.Processed, OnSuccessAction.KeepSource)
                : ($"file-{i:D6}.txt", OperationKind.SkippedByFilter, (OnSuccessAction?)null);
        }
        return SourceReport(files);
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
        // The kept-around original is emitted by the server as a destination op with SourceIndex == -1, so
        // it is a row of the Destinations half and of nothing else. On the Sources side clobber rolls its
        // real targets (Overwrite + Rename) up into the one glyph the row shows — read from the mask the
        // plan recorded per source, because a page of sources.ndjsonl never sees the destination half.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        DryRunFileRow clobber = viewModel.Sources.VisibleRows.Single(r => r.SourcePath.EndsWith("clobber.txt"));
        Assert.True(clobber.HasTargetKind);
        // Overwrite outranks Rename, so that is the glyph shown — and the kept original, which is not
        // clobber's target at all, contributes nothing to it.
        Assert.Equal("IconOverwrite", clobber.PrimaryKindIconKey);

        DryRunDestinationRow kept = viewModel.Destinations.VisibleRows
            .Single(r => r.Primary.TargetPath == @"C:\t2\clobber.txt");
        Assert.True(kept.Primary.IsUntouched);
        Assert.False(kept.HasSource);   // every paged destination row stands on its own
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

    // The order rows come out in is no longer decided here. It is decided by the ONE comparator that can
    // decide it — RunSnapshotOrder's, over the rows as they stream past during planning — and pinned by
    // RunSnapshotPagingTests, which asserts that paging a half end to end reproduces exactly that order.
    // The client reads a window of it and sorts nothing, so a client-side test of a 6,000-row sort would
    // now be asserting against a fixture rather than against the app. What still belongs here is that the
    // two tabs LINE UP, which Sources_and_destinations_share_relative_path_ordering covers.
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
    public async Task Replicated_file_shows_one_row_per_resulting_destination()
    {
        // One source fanned out to three targets → three rows, one per resulting path.
        //
        // It used to collapse into one grouped row listing all three, and that could not survive paging:
        // the destination half's index space is per OPERATION — that is what the order file and the block
        // index are built over — so a page holds operations, and a fan-out group could straddle a page
        // boundary with no way to reunite it. The whole-plan counts are unaffected: they were always over
        // the individual destinations rather than over the collapsed row.
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

        Assert.Equal(3, viewModel.Destinations.VisibleRows.Count);
        Assert.All(viewModel.Destinations.VisibleRows, r => Assert.Equal("report.docx", r.Primary.FileName));
        Assert.Equal(
            [@"C:\a\report.docx", @"C:\b\report.docx", @"C:\c\report.docx"],
            viewModel.Destinations.VisibleRows.Select(r => r.Primary.TargetPath).Order().ToList());
        Assert.Equal(2, viewModel.Destinations.VisibleRows.Count(r => r.Primary.IsNew));
        Assert.Equal(1, viewModel.Destinations.VisibleRows.Count(r => r.Primary.IsOverwritten));
        // And the whole-plan counts are what they always were.
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
        await RunPlans.SettleAsync(viewModel);
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
        await RunPlans.SettleAsync(viewModel);
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
        await RunPlans.SettleAsync(viewModel);
        Assert.True(viewModel.Sources.AnyStatusSelected);
        Assert.EndsWith("clobber.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);

        // Untouched → the filtered-out + unchanged files (junk.tmp + same.txt).
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = false;
        viewModel.Sources.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        await RunPlans.SettleAsync(viewModel);
        Assert.Equal(2, viewModel.Sources.VisibleRows.Count);
        Assert.All(viewModel.Sources.VisibleRows, r => Assert.True(r.IsUntouched));

        // A view filter never changes the whole-run summary counts.
        Assert.Equal(1, viewModel.Sources.DeletedCount);
        Assert.Equal(2, viewModel.Sources.UntouchedCount);
    }

    [Fact]
    public async Task Each_status_chip_asks_the_service_for_the_kinds_it_stands_for()
    {
        // The chip → OperationKind mapping is the client's half of the filter contract: the service does
        // set membership on an enum it owns and never learns what a chip is. Asserting the REQUEST rather
        // than the resulting rows is the point — a mapping that drifted would still return plausible rows,
        // just the wrong ones, and only the request shows which.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        // Untouched, on the source side, is the two SKIPPED kinds.
        gateway.ViewRequests.Clear();
        viewModel.Sources.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        await RunPlans.SettleAsync(viewModel);
        GetRunPlanViewRequest sources = Assert.Single(gateway.ViewRequests);
        Assert.Equal(
            [OperationKind.SkippedByFilter, OperationKind.SkippedUnchanged],
            sources.Kinds!.Order().ToList());
        Assert.False(sources.IncludeDestructiveDisposition);

        // Deleted is NOT a kind — it is a destructive source disposition — so it rides as its own flag,
        // ORed with the kinds, because the tab treats its chips as alternatives.
        gateway.ViewRequests.Clear();
        viewModel.Sources.StatusFilters.Single(f => f.Key == "untouched").IsSelected = false;
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = true;
        await RunPlans.SettleAsync(viewModel);
        GetRunPlanViewRequest deleted = gateway.ViewRequests[^1];
        Assert.True(deleted.IncludeDestructiveDisposition);
        Assert.Empty(deleted.Kinds!);   // empty keeps nothing on its own; the flag ORs the rows back in

        // On the destination side every chip IS a set of kinds, and New covers a conflict rename too:
        // the server already split that into a new file at the suffixed path plus the kept original.
        gateway.ViewRequests.Clear();
        viewModel.Destinations.StatusFilters.Single(f => f.Key == "new").IsSelected = true;
        await RunPlans.SettleAsync(viewModel);
        GetRunPlanViewRequest destinations = Assert.Single(gateway.ViewRequests);
        Assert.Equal(RunPlanSide.Destinations, destinations.Side);
        Assert.Equal([OperationKind.New, OperationKind.Rename], destinations.Kinds!.Order().ToList());
    }

    [Fact]
    public async Task Deselecting_every_facet_keeps_nothing_rather_than_everything()
    {
        // A filter the user can express: null means "keep every root", an EMPTY list means "keep none".
        // Widening the second back to the first would show exactly the rows they just excluded.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = MultiSourceReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        gateway.ViewRequests.Clear();
        foreach (DryRunFacetRow facet in viewModel.Sources.SourceFacets)
            facet.IsSelected = false;
        await RunPlans.SettleAsync(viewModel);

        Assert.Empty(gateway.ViewRequests[^1].SourceRoots!);
        Assert.Empty(viewModel.Sources.VisibleRows);
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
        await RunPlans.SettleAsync(viewModel);
        var names = viewModel.Sources.VisibleRows.Select(r => r.FileName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("fresh.txt", names);
        Assert.Contains("clobber.txt", names);

        // The clear command drops every selection and shows everything again.
        viewModel.Sources.ClearStatusFiltersCommand.Execute(null);
        await RunPlans.SettleAsync(viewModel);
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
        await RunPlans.SettleAsync(viewModel);
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
        await RunPlans.SettleAsync(viewModel);
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
        await RunPlans.SettleAsync(viewModel);
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
        await RunPlans.SettleAsync(viewModel);
        Assert.EndsWith("fresh.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);

        viewModel.Sources.SearchText = @"t2\clobber";   // only clobber has a t2 target
        await RunPlans.SettleAsync(viewModel);
        Assert.EndsWith("clobber.txt", Assert.Single(viewModel.Sources.VisibleRows).SourcePath);

        viewModel.Sources.SearchText = "";
        await RunPlans.SettleAsync(viewModel);
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
    public async Task A_preview_opens_the_plan_of_the_run_it_was_given()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);

        // The rows come from the RUN's frozen snapshot, not from a fresh simulation — that is the whole
        // point, and it is why the gateway has no dry-run method left to reach for. Every call the preview
        // makes is scoped to that run: its header, and a view over each half.
        Assert.Equal(runId, Assert.Single(gateway.RunPlanOpenCalls));
        Assert.Equal(runId, Assert.Single(gateway.GetRunDetailCalls));
        Assert.All(gateway.ViewRequests, r => Assert.Equal(runId, r.RunId));
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

        // The phase transitions: the planning caption from the moment Preview is pressed, the opening
        // caption while the run's header and views are read, and empty once the rows are up.
        Assert.Equal("Working out what this will do…", observed.First());
        Assert.Contains("Opening the plan…", observed);
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
    public async Task Every_source_row_in_a_plan_replay_says_what_happens_at_its_target()
    {
        // A source page carries no destination operations — they are the other half of the plan, and a
        // window onto one half does not read the other. What the row needs is the KIND, and the plan
        // recorded that per source as a mask so the glyph survives without the operations.
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = AdditivePlanReplay(files: 1);

        await RunPlans.PreviewAsync(viewModel, gateway, copies: 1);

        DryRunFileRow row = Assert.Single(viewModel.Sources.VisibleRows);
        Assert.True(row.HasTargetKind);
        Assert.Equal("IconAdd", row.PrimaryKindIconKey);   // a plain new write

        // And the destination it lands at is a row of the Destinations half.
        Assert.Equal(
            @"D:\d\file-0.txt",
            Assert.Single(viewModel.Destinations.VisibleRows).Primary.TargetPath);
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

    /// <summary>Previously this view model declined the previous run itself. It must NOT any more: a
    /// preview is retained per PROFILE now, and this type has no idea which profile a run belongs to — so
    /// declining here would kill the result held for a different profile. <c>PreviewStore</c> owns the
    /// obligation, and <c>PreviewStoreTests</c> pins that it is honoured.</summary>
    [Fact]
    public async Task A_superseding_preview_leaves_the_previous_run_for_the_store_to_answer()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        Guid second = await RunPlans.PreviewAsync(viewModel, gateway);

        Assert.Empty(gateway.ApproveRunCalls);
        Assert.Equal(second, viewModel.PendingRunId);
    }

    /// <summary>Closing the profile drops the FOOTER's state but keeps the run alive, so re-selecting the
    /// profile can re-stream its rows. This is the change that makes a preview survive navigation; the
    /// snapshot it holds is released by <c>PreviewStore.DiscardAllAsync</c> on window close.</summary>
    [Fact]
    public async Task Closing_the_profile_keeps_the_run_parked_so_the_preview_can_be_reopened()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.ClearProfile();

        Assert.Empty(gateway.ApproveRunCalls);
        Assert.Null(viewModel.PendingRunId);   // nothing on screen to approve
        Assert.False(viewModel.HasReport);
    }

    /// <summary>The age comes from the PLAN's own timestamp, not from when the tab rendered it. The second
    /// case is the one that matters: a long-ago plan reads stale the moment it is shown, which is exactly
    /// what a preview restored after the app sat open all afternoon must do.</summary>
    [Fact]
    public async Task A_plan_carries_the_age_its_staleness_is_measured_from()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await RunPlans.PreviewAsync(viewModel, gateway, plannedAtUtc: now);
        Assert.Equal(now, viewModel.PreviewTakenAtUtc);
        Assert.True(viewModel.HasPreviewAge);
        Assert.False(viewModel.IsPreviewStale);

        await RunPlans.PreviewAsync(viewModel, gateway, plannedAtUtc: now - TimeSpan.FromHours(3));
        Assert.True(viewModel.IsPreviewStale);
        Assert.Contains("3 hr ago", viewModel.PreviewAgeText);
    }

    /// <summary>The threshold really governs the flag, and the age comes from the PLAN's timestamp rather
    /// than from when the tab rendered it — which is what makes a reopened preview report its true age
    /// instead of looking freshly taken.</summary>
    [Fact]
    public async Task A_preview_older_than_the_threshold_reads_as_stale()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        var (viewModel, gateway) = NewViewModel(time: clock);
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        viewModel.PreviewStaleAfter = TimeSpan.FromMinutes(15);
        await RunPlans.PreviewAsync(viewModel, gateway, plannedAtUtc: clock.GetUtcNow());

        Assert.False(viewModel.IsPreviewStale);

        clock.Advance(TimeSpan.FromMinutes(14));
        viewModel.RefreshPreviewAge();
        Assert.False(viewModel.IsPreviewStale);
        Assert.Contains("14 min ago", viewModel.PreviewAgeText);

        clock.Advance(TimeSpan.FromMinutes(2));
        viewModel.RefreshPreviewAge();
        Assert.True(viewModel.IsPreviewStale);
        Assert.Contains("may be out of date", viewModel.PreviewStaleNotice);
    }

    /// <summary>A restore whose run has gone — the ORDINARY case after a service restart, since the engine
    /// sweeps its runs directory at startup. It must read as an expired preview, not as a failure.</summary>
    [Fact]
    public async Task Restoring_a_preview_whose_run_has_gone_reports_expiry_without_an_error()
    {
        var (viewModel, gateway) = NewViewModel();
        Guid runId = Guid.NewGuid();
        gateway.RunPlanResults[runId] = new IpcError("RUN_NOT_FOUND", $"no run with id {runId}");

        bool restored = await viewModel.RestoreAsync(
            new StoredPreview(RunPlans.Planned(runId, viewModel.ProfileId!.Value), DateTimeOffset.UtcNow));

        Assert.False(restored);
        Assert.Null(viewModel.ErrorMessage);       // not a failure — no danger banner
        Assert.Null(viewModel.PendingRunId);
        Assert.Null(viewModel.PreviewTakenAtUtc);
        Assert.Contains("expired", viewModel.EmptyStateText);
    }

    /// <summary>A restore streams the run's frozen plan back through the SAME ingest path a fresh preview
    /// uses, and raises the footer again — that is what makes the result re-approvable after navigating
    /// away and back.</summary>
    [Fact]
    public async Task Restoring_a_preview_brings_back_its_rows_and_its_footer()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);
        RunPlannedEvent planned = RunPlans.Planned(runId, viewModel.ProfileId!.Value);
        viewModel.ClearProfile();
        Assert.False(viewModel.HasReport);

        bool restored = await viewModel.RestoreAsync(new StoredPreview(planned, planned.AtUtc));

        Assert.True(restored);
        Assert.True(viewModel.HasReport);
        Assert.Equal(runId, viewModel.PendingRunId);
        Assert.Equal(planned.AtUtc, viewModel.PreviewTakenAtUtc);
    }

    /// <summary>REGRESSION. Discarding a run that was still PLANNING left the tab's progress bar spinning
    /// forever: the discard handler checked only <c>PendingRunId</c>, which is null during a scan, and the
    /// late <c>run-planned</c> that would otherwise have ended the wait was no longer recognized as this
    /// window's — the shell drops the id at the same moment. Nothing was left to stop it.</summary>
    [Fact]
    public void Discarding_a_run_that_is_still_PLANNING_stops_the_preview_spinner()
    {
        var (viewModel, _) = NewViewModel();
        Guid runId = Guid.NewGuid();
        viewModel.BeginPlanning();
        viewModel.PlanningStarted(runId);
        Assert.True(viewModel.IsPreviewing);

        viewModel.ForgetDiscardedRun(runId);

        Assert.False(viewModel.IsPreviewing);
        Assert.Null(viewModel.PlanningRunId);
        Assert.Equal("", viewModel.RunStatusText);
        Assert.Contains("discarded", viewModel.EmptyStateText);
        Assert.Null(viewModel.ErrorMessage);   // the user did this; it is not a failure
    }

    /// <summary>Discarding a run whose plan is already on screen drops the footer — leaving it would offer
    /// to approve a run the engine has forgotten.</summary>
    [Fact]
    public async Task Discarding_a_run_whose_plan_is_on_screen_drops_the_footer()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);
        Assert.Equal(runId, viewModel.PendingRunId);

        viewModel.ForgetDiscardedRun(runId);

        Assert.Null(viewModel.PendingRunId);
        Assert.Contains("discarded", viewModel.EmptyStateText);
    }

    /// <summary>A discard for some OTHER run must not disturb the preview on screen — the queue lists every
    /// run, so discarding one is not a statement about the one this tab is showing.</summary>
    [Fact]
    public async Task Discarding_an_unrelated_run_leaves_the_preview_alone()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        Guid runId = await RunPlans.PreviewAsync(viewModel, gateway);

        viewModel.ForgetDiscardedRun(Guid.NewGuid());

        Assert.Equal(runId, viewModel.PendingRunId);
        Assert.True(viewModel.HasReport);
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
        // A New destination names a path nothing is at yet, so its size is the incoming content's — which
        // a destination page carries as the operation's own size rather than a subject file's.
        Assert.Equal("1.5 KB", Assert.Single(viewModel.Destinations.VisibleRows).Primary.SizeText);
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
    public async Task Nonzero_debounce_coalesces_rapid_search_changes_to_the_final_term()
    {
        FakeIpcGateway gateway = new()
        {
            DryRunResult = SourceReport(
                ("alpha.txt", OperationKind.Processed, OnSuccessAction.KeepSource),
                ("beta.txt", OperationKind.Processed, OnSuccessAction.KeepSource),
                ("gamma.txt", OperationKind.Processed, OnSuccessAction.KeepSource)),
        };
        DryRunViewModel viewModel = new(gateway, searchDebounce: TimeSpan.FromMilliseconds(60));
        viewModel.SetProfile(Guid.NewGuid(), "P");
        await RunPlans.PreviewAsync(viewModel, gateway);
        DryRunSourcesTab tab = viewModel.Sources;
        Assert.Equal(3, tab.VisibleRows.Count);

        tab.SearchText = "alpha";
        tab.SearchText = "beta";
        tab.SearchText = "gamma";

        // The debounce delays the request, so no intermediate term has reached the service yet.
        Assert.Empty(gateway.ViewRequests.Where(r => r.Search is not null));
        Assert.Equal(3, tab.VisibleRows.Count);

        // After the window lapses exactly one view is built, for the final term only.
        await WaitUntilAsync(() => tab.VisibleRows.Count == 1);
        await RunPlans.SettleAsync(viewModel);

        Assert.Equal("gamma", Assert.Single(gateway.ViewRequests.Where(r => r.Search is not null)).Search);
        DryRunFileRow only = Assert.Single(tab.VisibleRows);
        Assert.EndsWith("gamma.txt", only.SourcePath);
    }

    [Fact]
    public async Task Rapid_filter_toggles_on_a_large_report_coalesce_to_the_latest_state()
    {
        var (viewModel, _, _) = await RunPlans.OpenAsync(ManyRowsReport(6_000));
        DryRunSourcesTab tab = viewModel.Sources;
        Assert.Equal(6_000, tab.VisibleRows.Count);

        // Click chips in quick succession; each toggle supersedes the rebuild before it, so only
        // the last filter state may publish.
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = false;
        tab.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        await tab.PendingRebuild;
        await RunPlans.SettleAsync(viewModel);

        Assert.Equal(3_000, tab.VisibleRows.Count);
        Assert.All(tab.VisibleRows.Take(20), r => Assert.True(r.IsUntouched));
    }

    [Fact]
    public async Task Loading_a_new_plan_supersedes_an_in_flight_rebuild()
    {
        var (viewModel, gateway, _) = await RunPlans.OpenAsync(ManyRowsReport(6_000));
        DryRunSourcesTab tab = viewModel.Sources;

        // Hold the service mid-filter, then load a replacement plan while it is in flight. The load
        // cancels the rebuild, and the superseded rebuild must never publish against the old plan.
        gateway.ViewGate = new TaskCompletionSource();
        tab.StatusFilters.Single(f => f.Key == "processed").IsSelected = true;
        Task superseded = tab.PendingRebuild;

        gateway.DryRunResult = SourceReport(("alpha.txt", OperationKind.Processed, OnSuccessAction.KeepSource));
        gateway.ViewGate = null;
        await RunPlans.PreviewAsync(viewModel, gateway);
        gateway.ViewGate?.SetResult();
        await superseded;

        DryRunFileRow only = Assert.Single(tab.VisibleRows);
        Assert.Equal("alpha.txt", only.FileName);
    }

    [Fact]
    public async Task Large_rebuilds_flag_IsRebuilding_until_they_publish()
    {
        var (viewModel, gateway, _) = await RunPlans.OpenAsync(ManyRowsReport(6_000));
        DryRunSourcesTab tab = viewModel.Sources;
        Assert.False(tab.IsRebuilding);

        // The flag is set synchronously, before the view request goes out, and cleared by the publish —
        // the window the view's loading overlay is visible for. Gated so that window is observable at
        // all: against an instant fake the whole rebuild would finish inside the property setter.
        gateway.ViewGate = new TaskCompletionSource();
        tab.StatusFilters.Single(f => f.Key == "untouched").IsSelected = true;
        Assert.True(tab.IsRebuilding);

        gateway.ViewGate.SetResult();
        await tab.PendingRebuild;
        Assert.False(tab.IsRebuilding);
    }

    [Fact]
    public async Task Small_rebuilds_never_flag_IsRebuilding()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await RunPlans.PreviewAsync(viewModel, gateway);

        // Under the threshold there is nothing slow to explain, so the overlay must never flicker.
        viewModel.Sources.StatusFilters.Single(f => f.Key == "deleted").IsSelected = true;
        Assert.False(viewModel.Sources.IsRebuilding);
        await RunPlans.SettleAsync(viewModel);
        Assert.Single(viewModel.Sources.VisibleRows);
    }

    [Fact]
    public async Task A_view_the_service_cannot_build_leaves_the_current_rows_alone()
    {
        // A failed filter is not a reason to blank the panel: what is on screen is still a truthful view
        // of the plan, and the next keystroke retries anyway.
        var (viewModel, gateway, _) = await RunPlans.OpenAsync(SampleReport(Guid.NewGuid()));
        DryRunSourcesTab tab = viewModel.Sources;
        int before = tab.VisibleRows.Count;

        gateway.ViewError = new IpcError("RUN_NOT_FOUND", "the run is gone");
        tab.SearchText = "fresh";
        await tab.PendingRebuild;

        Assert.Equal(before, tab.VisibleRows.Count);
        Assert.False(tab.IsRebuilding);
        Assert.Null(viewModel.ErrorMessage);   // a banner would blame the user for typing
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(condition(), "condition was not met within the timeout");
    }
}
