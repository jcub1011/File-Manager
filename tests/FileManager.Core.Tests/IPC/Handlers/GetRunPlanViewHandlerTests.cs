using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

/// <summary>Filtering a plan half service-side — the search box, the facet bar and the status chips.
///
/// <para>These moved off the client because they used to scan the rows it was holding, which only ever
/// worked because it held all of them. What must survive the move is the MEANING of each control: a
/// filtered list has to contain exactly the rows the old client-side predicate kept, in the same order
/// the unfiltered list would have shown them.</para></summary>
public sealed class GetRunPlanViewHandlerTests
{
    private sealed record Fixture(
        RunPlanHarness H, string Dir, Guid RunId,
        GetRunPlanViewHandler View, GetRunPlanPageHandler Page) : IDisposable
    {
        public void Dispose() => H.Dispose();
    }

    private static async Task<Fixture> PlannedAsync(string label)
    {
        RunPlanHarness h = new(label);
        // A shape with something for every predicate to bite on: files that copy, a file already
        // identical (untouched), a filtered-out file, and orphans on the destination side.
        h.WriteSource(Path.Combine("keep", "alpha.txt"), "alpha");
        h.WriteSource(Path.Combine("keep", "beta.txt"), "beta");
        h.WriteSource(Path.Combine("other", "gamma.txt"), "gamma");
        h.WriteSource(Path.Combine("other", "alpha-too.txt"), "delta");
        h.WriteTarget(Path.Combine("keep", "alpha.txt"), "alpha");   // identical → SkippedUnchanged
        h.WriteTarget("orphan-one.txt", "gone");
        h.WriteTarget("orphan-two.txt", "gone");

        Guid runId = Guid.NewGuid();
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), runId: runId);
        SingleSnapshotCoordinator coordinator = new(runId, dir);
        return new Fixture(h, dir, runId,
            new GetRunPlanViewHandler(coordinator, NullLogger<GetRunPlanViewHandler>.Instance),
            new GetRunPlanPageHandler(coordinator, NullLogger<GetRunPlanPageHandler>.Instance));
    }

    private static async Task<RunPlanViewResponse> ViewAsync(Fixture f, GetRunPlanViewRequest request) =>
        Assert.IsType<RunPlanViewResponse>(await f.View.HandleAsync(request));

    /// <summary>The view's rows, read back through the page handler exactly as a client would.</summary>
    private static async Task<List<string>> RowsAsync(Fixture f, RunPlanSide side, RunPlanViewResponse view)
    {
        IpcResponse response = await f.Page.HandleAsync(new GetRunPlanPageRequest
        {
            RunId = f.RunId, Side = side, First = 0, Count = view.RowCount, ViewId = view.ViewId,
        });
        DryRunChunkResponse chunk = Assert.IsType<DryRunChunkResponse>(response);
        string[] dirs = DryRunDirectoryTable.Materialize([.. DryRunColumns.ToDirectoryRecords(chunk)]);
        DryRunOperationColumns ops = side == RunPlanSide.Sources
            ? chunk.SourceOperations
            : chunk.DestinationOperations;
        List<string> rows = [];
        for (int i = 0; i < ops.Count; i++)
            rows.Add(Path.Join(dirs[ops.DirIndex[i]], ops.FileName[i]));
        return rows;
    }

    [Fact]
    public async Task An_empty_filter_selects_everything_and_builds_no_view_file()
    {
        // The state a freshly opened preview is in, and the one that must cost nothing: no scan, no
        // file, and the plan's own order.
        using Fixture f = await PlannedAsync("view-none");

        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources,
        });

        Assert.Null(view.ViewId);
        Assert.Equal(RunPlanHarness.Sources(f.Dir).Count, view.RowCount);
        Assert.Empty(Directory.GetFiles(f.Dir, "view-*.ord"));
    }

    [Fact]
    public async Task Search_matches_a_source_by_its_own_path()
    {
        using Fixture f = await PlannedAsync("view-search");

        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Search = "alpha",
        });
        List<string> rows = await RowsAsync(f, RunPlanSide.Sources, view);

        Assert.Equal(view.RowCount, rows.Count);
        Assert.All(rows, r => Assert.Contains("alpha", r, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.EndsWith("alpha.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, r => r.EndsWith("alpha-too.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_also_matches_a_source_by_where_it_is_GOING()
    {
        // What the Sources tab has always done, and the awkward half of the move: the term is matched
        // against a row's destinations as well as its own path, and those live in a different file.
        using Fixture f = await PlannedAsync("view-search-dest");

        // Every destination sits under the target root, so a term from the target path must select
        // every row that writes there — while matching none of the source paths itself.
        string targetLeaf = Path.GetFileName(f.H.TargetDir);
        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Search = targetLeaf,
        });

        Assert.DoesNotContain(targetLeaf, f.H.SourceDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(view.RowCount > 0, "a term matching only destinations must still select their sources");
        List<string> rows = await RowsAsync(f, RunPlanSide.Sources, view);
        Assert.All(rows, r => Assert.DoesNotContain(targetLeaf, r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_status_filter_keeps_only_the_selected_kinds()
    {
        using Fixture f = await PlannedAsync("view-status");
        List<RunSourceItem> all = RunPlanHarness.Sources(f.Dir);
        int unchanged = all.Count(s => s.Kind is OperationKind.SkippedUnchanged);
        Assert.True(unchanged > 0, "the fixture needs an already-identical file for this to mean anything");

        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Kinds = [OperationKind.SkippedUnchanged],
        });

        Assert.Equal(unchanged, view.RowCount);
    }

    [Fact]
    public async Task Filters_AND_together()
    {
        using Fixture f = await PlannedAsync("view-and");

        RunPlanViewResponse searchOnly = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Search = "alpha",
        });
        RunPlanViewResponse both = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources,
            Search = "alpha", Kinds = [OperationKind.Processed],
        });

        Assert.True(both.RowCount < searchOnly.RowCount,
            "adding a status filter to a search must narrow it, not widen it");
        List<string> rows = await RowsAsync(f, RunPlanSide.Sources, both);
        Assert.All(rows, r => Assert.Contains("alpha", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_filtered_view_keeps_the_unfiltered_display_order()
    {
        // The scans run in FILE order; the user reads DISPLAY order. A filtered list that reordered its
        // rows relative to the unfiltered one would make the two tabs disagree about where a file sits,
        // which is the whole thing the plan-time sort exists to guarantee.
        using Fixture f = await PlannedAsync("view-order");

        RunPlanViewResponse all = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources,
        });
        List<string> unfiltered = await RowsAsync(f, RunPlanSide.Sources, all);

        RunPlanViewResponse filtered = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Kinds = [OperationKind.Processed],
        });
        List<string> rows = await RowsAsync(f, RunPlanSide.Sources, filtered);

        Assert.Equal(unfiltered.Where(rows.Contains), rows);
    }

    [Fact]
    public async Task The_destination_side_filters_orphans_by_kind()
    {
        using Fixture f = await PlannedAsync("view-dest-kind");

        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Destinations, Kinds = [OperationKind.Deleted],
        });
        List<string> rows = await RowsAsync(f, RunPlanSide.Destinations, view);

        Assert.Equal(RunPlanHarness.Deletes(f.Dir).Count, view.RowCount);
        foreach (RunDeleteItem orphan in RunPlanHarness.Deletes(f.Dir))
            Assert.Contains(orphan.Path, rows, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_identical_filter_reuses_the_view_it_already_built()
    {
        // Re-selecting a facet must not rescan the plan. The file is the cache, keyed by the filter.
        using Fixture f = await PlannedAsync("view-cache");
        GetRunPlanViewRequest request = new()
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Search = "alpha",
        };

        RunPlanViewResponse first = await ViewAsync(f, request);
        RunPlanViewResponse second = await ViewAsync(f, request with { });

        Assert.Equal(first.ViewId, second.ViewId);
        Assert.Equal(first.RowCount, second.RowCount);
        Assert.Single(Directory.GetFiles(f.Dir, "view-*.ord"));
    }

    [Fact]
    public async Task Cached_views_are_bounded_so_typing_in_the_search_box_cannot_fill_the_run_directory()
    {
        // Every distinct filter caches a file, and the search box produces a distinct filter per
        // debounced keystroke — so typing a word would otherwise leave one per prefix, for as long as
        // the run is open. Twelve searches against a cache of eight.
        using Fixture f = await PlannedAsync("view-evict");
        for (int i = 0; i < 12; i++)
        {
            await ViewAsync(f, new GetRunPlanViewRequest
            {
                RunId = f.RunId, Side = RunPlanSide.Sources, Search = $"term-{i}",
            });
        }

        Assert.True(
            Directory.GetFiles(f.Dir, "view-*.ord").Length <= 8,
            $"kept {Directory.GetFiles(f.Dir, "view-*.ord").Length} view files, above the cache bound");
    }

    [Fact]
    public async Task A_filter_the_user_keeps_returning_to_survives_the_searches_typed_in_between()
    {
        // Eviction is by RECENCY, not creation order: a facet selection re-applied throughout a session
        // must not be thrown away because of the searches typed since, or every return to it rescans.
        using Fixture f = await PlannedAsync("view-evict-recency");
        GetRunPlanViewRequest kept = new()
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Kinds = [OperationKind.Processed],
        };
        RunPlanViewResponse first = await ViewAsync(f, kept);

        for (int i = 0; i < 6; i++)
        {
            await ViewAsync(f, new GetRunPlanViewRequest
            {
                RunId = f.RunId, Side = RunPlanSide.Sources, Search = $"noise-{i}",
            });
            await ViewAsync(f, kept);   // the user comes back to it
        }
        for (int i = 6; i < 12; i++)
        {
            await ViewAsync(f, new GetRunPlanViewRequest
            {
                RunId = f.RunId, Side = RunPlanSide.Sources, Search = $"noise-{i}",
            });
        }

        Assert.True(File.Exists(Path.Combine(f.Dir, $"view-{first.ViewId}.ord")),
            "the repeatedly-used view was evicted in favour of one-off searches");
    }

    [Fact]
    public async Task A_stale_view_id_falls_back_to_the_full_list_rather_than_blanking_the_panel()
    {
        using Fixture f = await PlannedAsync("view-stale");
        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, Search = "alpha",
        });
        foreach (string file in Directory.GetFiles(f.Dir, "view-*.ord"))
            File.Delete(file);

        IpcResponse response = await f.Page.HandleAsync(new GetRunPlanPageRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, First = 0, Count = 100, ViewId = view.ViewId,
        });

        DryRunChunkResponse chunk = Assert.IsType<DryRunChunkResponse>(response);
        Assert.Equal(RunPlanHarness.Sources(f.Dir).Count, chunk.SourceOperations.Count);
    }

    [Fact]
    public async Task An_empty_root_selection_keeps_nothing_rather_than_everything()
    {
        // Deselecting every facet is a filter the user can actually express. Widening it back to
        // "everything" would show rows they had just excluded.
        using Fixture f = await PlannedAsync("view-empty-roots");

        RunPlanViewResponse view = await ViewAsync(f, new GetRunPlanViewRequest
        {
            RunId = f.RunId, Side = RunPlanSide.Sources, SourceRoots = [],
        });

        Assert.Equal(0, view.RowCount);
    }
}
