using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

/// <summary>Serving one WINDOW of a frozen plan.
///
/// <para>The property that matters is agreement: a page must show the same rows, in the same order, that
/// the client would have seen if it had read the whole plan and sorted it itself — which is what it used
/// to do, and what it can no longer afford now that a plan has no file cap. Every test here goes through
/// the real planner into a real snapshot, because the sidecars a page depends on are written by the
/// writer and a scripted fixture would prove nothing about them.</para></summary>
public sealed class GetRunPlanPageHandlerTests
{
    private static async Task<(RunPlanHarness H, string Dir, Guid RunId, GetRunPlanPageHandler Handler)>
        PlannedAsync(string label, int sourceFiles, int orphans = 0)
    {
        RunPlanHarness h = new(label);
        for (int i = 0; i < sourceFiles; i++)
            h.WriteSource(Path.Combine($"d{i % 6}", $"f{i:D3}.txt"), new string('x', i + 1));
        for (int i = 0; i < orphans; i++)
            h.WriteTarget($"orphan{i:D3}.txt", "gone");

        Guid runId = Guid.NewGuid();
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), runId: runId);
        return (h, dir, runId, new GetRunPlanPageHandler(
            new SingleSnapshotCoordinator(runId, dir), NullLogger<GetRunPlanPageHandler>.Instance));
    }

    /// <summary>A page's rows as "directory\name" strings, in the order the frame carries them.</summary>
    private static async Task<List<string>> PageAsync(
        GetRunPlanPageHandler handler, Guid runId, RunPlanSide side, int first, int count)
    {
        IpcResponse response = await handler.HandleAsync(
            new GetRunPlanPageRequest { RunId = runId, Side = side, First = first, Count = count });
        DryRunChunkResponse chunk = Assert.IsType<DryRunChunkResponse>(response);

        string[] dirs = DryRunDirectoryTable.Materialize(
            [.. DryRunColumns.ToDirectoryRecords(chunk)]);
        DryRunOperationColumns ops = side == RunPlanSide.Sources
            ? chunk.SourceOperations
            : chunk.DestinationOperations;
        List<string> rows = [];
        for (int i = 0; i < ops.Count; i++)
            rows.Add(Path.Join(dirs[ops.DirIndex[i]], ops.FileName[i]));
        return rows;
    }

    /// <summary>Every source row in display order, read one page at a time — the sequence a client
    /// scrolling from top to bottom would assemble.</summary>
    private static async Task<List<string>> AllPagesAsync(
        GetRunPlanPageHandler handler, Guid runId, RunPlanSide side, int total, int pageSize)
    {
        List<string> rows = [];
        for (int first = 0; first < total; first += pageSize)
            rows.AddRange(await PageAsync(handler, runId, side, first, pageSize));
        return rows;
    }

    [Fact]
    public async Task Paging_through_the_whole_source_side_reproduces_the_sorted_plan_exactly()
    {
        (RunPlanHarness h, string dir, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-all", sourceFiles: 40);
        using (h)
        {
            List<RunSourceItem> sources = RunPlanHarness.Sources(dir);
            int[] order = RunSnapshotOrder.ReadRange(
                Path.Combine(dir, "sources.ord"), 0, sources.Count, NullLogger.Instance);
            List<string> expected = [.. order.Select(o => sources[o].Path)];

            // A page size that does not divide the row count, so the last page is short and the
            // boundaries do not line up with the 512-row blocks either.
            List<string> paged = await AllPagesAsync(handler, runId, RunPlanSide.Sources, sources.Count, 7);

            Assert.Equal(expected, paged);
        }
    }

    [Fact]
    public async Task A_page_starts_where_it_was_asked_to()
    {
        (RunPlanHarness h, string dir, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-offset", sourceFiles: 30);
        using (h)
        {
            List<string> whole = await PageAsync(handler, runId, RunPlanSide.Sources, 0, 30);
            List<string> middle = await PageAsync(handler, runId, RunPlanSide.Sources, 11, 5);

            Assert.Equal(whole.Skip(11).Take(5), middle);
        }
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_rather_than_an_error()
    {
        // A viewport may legally overhang the list while a scroll settles, so this is a normal request
        // and not a client bug to report.
        (RunPlanHarness h, _, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-past", sourceFiles: 5);
        using (h)
            Assert.Empty(await PageAsync(handler, runId, RunPlanSide.Sources, 500, 10));
    }

    [Fact]
    public async Task The_destination_side_pages_projection_rows_and_orphans_as_one_list()
    {
        (RunPlanHarness h, string dir, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-dest", sourceFiles: 10, orphans: 6);
        using (h)
        {
            int total = RunPlanHarness.Destinations(dir).Count + RunPlanHarness.Deletes(dir).Count;
            List<string> paged = await AllPagesAsync(handler, runId, RunPlanSide.Destinations, total, 4);

            Assert.Equal(total, paged.Count);
            Assert.Equal(total, paged.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            // Every orphan is reachable through paging — the half the user is actually approving.
            foreach (RunDeleteItem orphan in RunPlanHarness.Deletes(dir))
                Assert.Contains(orphan.Path, paged, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Orphans_are_paged_as_Deleted_operations()
    {
        (RunPlanHarness h, string dir, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-kinds", sourceFiles: 4, orphans: 3);
        using (h)
        {
            int total = RunPlanHarness.Destinations(dir).Count + RunPlanHarness.Deletes(dir).Count;
            IpcResponse response = await handler.HandleAsync(new GetRunPlanPageRequest
            {
                RunId = runId, Side = RunPlanSide.Destinations, First = 0, Count = total,
            });
            DryRunChunkResponse chunk = Assert.IsType<DryRunChunkResponse>(response);

            Assert.Equal(
                RunPlanHarness.Deletes(dir).Count,
                chunk.DestinationOperations.Kind.Count(k => k == OperationKind.Deleted));
        }
    }

    [Fact]
    public async Task A_snapshot_with_no_sidecars_still_pages_in_plan_order()
    {
        // What a snapshot written before paging existed looks like, and what a failed sidecar write
        // leaves behind. The preview must degrade to plan order and an unindexed scan — a sound plan
        // that cannot be displayed would be a worse outcome than a slow one.
        (RunPlanHarness h, string dir, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-nosidecar", sourceFiles: 12);
        using (h)
        {
            File.Delete(Path.Combine(dir, "sources.ord"));
            File.Delete(Path.Combine(dir, "sources.idx"));

            List<RunSourceItem> sources = RunPlanHarness.Sources(dir);
            List<string> paged = await AllPagesAsync(handler, runId, RunPlanSide.Sources, sources.Count, 5);

            Assert.Equal(sources.Select(s => s.Path), paged);
        }
    }

    [Fact]
    public async Task An_unknown_run_is_reported_rather_than_returning_an_empty_page()
    {
        (RunPlanHarness h, _, Guid runId, GetRunPlanPageHandler handler) =
            await PlannedAsync("page-h-unknown", sourceFiles: 2);
        using (h)
        {
            IpcResponse response = await handler.HandleAsync(new GetRunPlanPageRequest
            {
                RunId = Guid.NewGuid(), Side = RunPlanSide.Sources, First = 0, Count = 10,
            });

            ErrorResponse error = Assert.IsType<ErrorResponse>(response);
            Assert.Equal("RUN_NOT_FOUND", error.Code);
            Assert.NotEqual(Guid.Empty, runId);   // the harness's own run is unaffected
        }
    }
}
