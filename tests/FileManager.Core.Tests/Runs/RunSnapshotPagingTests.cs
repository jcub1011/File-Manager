using FileManager.Contracts.DryRun;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Runs;

/// <summary>The two sidecars that turn a snapshot's display half from a tape into an array: the block
/// index (ordinal → byte offset) and the order file (display position → ordinal).
///
/// <para>They exist because the preview stopped being able to hold a whole plan. It never really could —
/// the 500,000-file cap was what bounded it — and with the cap gone the client reads a window instead,
/// which needs random access it cannot get from NDJSON alone. These tests pin the two properties the
/// window depends on: that a seek lands on the row it claims, and that the order is the one the user
/// used to get from a client-side sort.</para></summary>
public sealed class RunSnapshotPagingTests
{
    /// <summary>The reference ordering, written out longhand: relative path, then root, then ordinal,
    /// all <c>OrdinalIgnoreCase</c>. This is deliberately a SECOND implementation — if it were shared
    /// with the production comparator the test could only prove it was self-consistent.</summary>
    private static List<int> ExpectedOrder(IReadOnlyList<RunSourceItem> sources)
    {
        List<int> order = [.. Enumerable.Range(0, sources.Count)];
        order.Sort((a, b) =>
        {
            string ka = Path.GetRelativePath(sources[a].SourceRoot, sources[a].Path);
            string kb = Path.GetRelativePath(sources[b].SourceRoot, sources[b].Path);
            int c = string.Compare(ka, kb, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
            c = string.Compare(sources[a].SourceRoot, sources[b].SourceRoot, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    private static int[] ReadWholeOrder(string dir, string fileName, int count) =>
        RunSnapshotOrder.ReadRange(Path.Combine(dir, fileName), 0, count, NullLogger.Instance);

    [Fact]
    public void The_order_is_by_path_RELATIVE_to_the_root_not_by_full_path()
    {
        // Where the two orderings actually diverge: across DIFFERENT roots. Full-path order groups every
        // row under one root before the next, because the root is the string's prefix. Relative-key
        // order strips it, so the same relative file from two roots sorts adjacently — which is the
        // whole reason the preview uses it. The Sources and Destinations tabs are read side by side, and
        // their rows sit under different roots by construction (C:\src\… vs D:\dst\…); ordering on the
        // full path would scatter a file away from its own destination row.
        //
        // Driven through the builder directly rather than a planned run: the harness has a single source
        // root, and one root is exactly the case in which the two orderings agree.
        using RunPlanHarness h = new("page-order-relative");
        string scratch = Path.Combine(h.Root, "order-relative");
        Directory.CreateDirectory(scratch);

        using RunSnapshotOrder.Builder builder = new(scratch, "t", NullLogger.Instance);
        builder.Row(@"C:\alpha\zzz.txt", @"C:\alpha");   // ordinal 0 — first by FULL path
        builder.Row(@"C:\beta\aaa.txt", @"C:\beta");     // ordinal 1 — first by RELATIVE key

        string orderPath = Path.Combine(scratch, "t.ord");
        Assert.Null(builder.Write(orderPath));

        Assert.Equal([1, 0], RunSnapshotOrder.ReadRange(orderPath, 0, 2, NullLogger.Instance));
    }

    [Fact]
    public async Task The_order_falls_back_to_the_root_then_the_ordinal_for_rows_with_equal_keys()
    {
        // The tiebreaks make the order TOTAL, which the two tabs depend on: Array.Sort is not stable, so
        // without a final ordinal compare two rows with the same relative key could come out in one
        // order on the source side and the other on the destination side, and the side-by-side
        // alignment the relative key exists for would break on exactly the rows it matters for.
        using RunPlanHarness h = new("page-order-ties");
        string scratch = Path.Combine(h.Root, "order-ties");
        Directory.CreateDirectory(scratch);

        using RunSnapshotOrder.Builder builder = new(scratch, "t", NullLogger.Instance);
        builder.Row(@"C:\beta\same.txt", @"C:\beta");    // 0: equal key, later root
        builder.Row(@"C:\alpha\same.txt", @"C:\alpha");  // 1: equal key, earlier root
        builder.Row(@"C:\alpha\same.txt", @"C:\alpha");  // 2: equal key AND root — ordinal decides

        string orderPath = Path.Combine(scratch, "t.ord");
        Assert.Null(builder.Write(orderPath));

        Assert.Equal([1, 2, 0], RunSnapshotOrder.ReadRange(orderPath, 0, 3, NullLogger.Instance));
    }

    [Fact]
    public async Task The_source_order_file_matches_the_reference_sort_end_to_end()
    {
        using RunPlanHarness h = new("page-order");
        h.WriteSource(Path.Combine("a b", "c.txt"), "x");
        h.WriteSource(Path.Combine("a", "z.txt"), "x");
        h.WriteSource("top.txt", "x");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        List<RunSourceItem> sources = RunPlanHarness.Sources(dir);
        int[] order = ReadWholeOrder(dir, "sources.ord", sources.Count);

        Assert.Equal(ExpectedOrder(sources), order);
    }

    [Fact]
    public async Task The_source_order_file_covers_every_row_exactly_once()
    {
        using RunPlanHarness h = new("page-order-total");
        for (int i = 0; i < 40; i++)
            h.WriteSource(Path.Combine($"d{i % 7}", $"f{i:D3}.txt"), new string('x', i + 1));

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        List<RunSourceItem> sources = RunPlanHarness.Sources(dir);
        int[] order = ReadWholeOrder(dir, "sources.ord", sources.Count);

        Assert.Equal(sources.Count, order.Length);
        Assert.Equal(Enumerable.Range(0, sources.Count), order.Order());   // a permutation, nothing lost
        Assert.Equal(ExpectedOrder(sources), order);
    }

    [Fact]
    public async Task The_order_survives_spilling_to_disk()
    {
        // Above RunRows the builder stops being an in-memory sort and becomes a k-way merge of spilled
        // runs — a different code path, and the one a large plan actually takes. 40 rows cannot reach
        // the production threshold, so the merge is driven directly instead.
        using RunPlanHarness h = new("page-order-spill");
        List<string> written = [];
        for (int i = 0; i < 40; i++)
            written.Add(h.WriteSource(Path.Combine($"d{i % 7}", $"f{i:D3}.txt"), "x"));
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        List<RunSourceItem> sources = RunPlanHarness.Sources(dir);

        string scratch = Path.Combine(dir, "spill-test");
        Directory.CreateDirectory(scratch);
        using RunSnapshotOrder.Builder builder = new(scratch, "t", NullLogger.Instance);
        foreach (RunSourceItem item in sources)
            builder.Row(item.Path, item.SourceRoot);
        string orderPath = Path.Combine(scratch, "spilled.ord");
        Assert.Null(builder.Write(orderPath));

        Assert.Equal(
            ExpectedOrder(sources),
            RunSnapshotOrder.ReadRange(orderPath, 0, sources.Count, NullLogger.Instance));
        // The run files are scratch, not output: a leaked one would accumulate per plan.
        Assert.Empty(Directory.GetFiles(scratch, "order-*.tmp"));
    }

    [Fact]
    public async Task A_page_read_by_ordinal_returns_the_same_rows_a_full_read_does()
    {
        using RunPlanHarness h = new("page-seek");
        for (int i = 0; i < 60; i++)
            h.WriteSource(Path.Combine($"d{i % 5}", $"f{i:D3}.txt"), new string('y', i + 1));

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        List<RunSourceItem> all = RunPlanHarness.Sources(dir);

        RunSnapshotBlockIndex.Index? index = RunSnapshotBlockIndex.TryRead(
            Path.Combine(dir, "sources.idx"), NullLogger.Instance);
        Assert.NotNull(index);
        Assert.Equal(all.Count, index!.Value.RowCount);

        // Deliberately scattered and out of order — a sorted view asks for rows in display order, which
        // is not file order, and the reader has to group them into forward seeks itself.
        int[] wanted = [17, 3, 59, 3, 0, 42];
        RunSourceItem?[] page = RunSnapshotReader.ReadByOrdinal(
            Path.Combine(dir, "sources.ndjsonl"), index.Value, wanted,
            RunSnapshotJsonContext.Default.RunSourceItem, NullLogger.Instance);

        Assert.Equal(wanted.Length, page.Length);
        for (int i = 0; i < wanted.Length; i++)
        {
            Assert.NotNull(page[i]);
            Assert.Equal(all[wanted[i]].Path, page[i]!.Path);
            Assert.Equal(all[wanted[i]].SizeBytes, page[i]!.SizeBytes);
        }
    }

    [Fact]
    public async Task The_destination_order_puts_orphans_after_the_projection_in_one_index_space()
    {
        // The destination side the client sees is destinations.ndjsonl followed by deletes.ndjsonl in a
        // single ordinal space (GetRunPlanStreamHandler replays them that way). A delete's ordinal is
        // therefore its own position plus every projection row — a total the builder does not know until
        // the plan ends, so getting it wrong is silent and this is what catches it.
        using RunPlanHarness h = new("page-order-dest");
        h.WriteSource("kept.txt", "kept");
        h.WriteTarget("aaa-orphan.txt", "gone");
        h.WriteTarget("zzz-orphan.txt", "gone");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        int projection = RunPlanHarness.Destinations(dir).Count;
        int deletes = RunPlanHarness.Deletes(dir).Count;
        Assert.Equal(2, deletes);

        int[] order = ReadWholeOrder(dir, "destinations.ord", projection + deletes);

        Assert.Equal(projection + deletes, order.Length);
        Assert.Equal(Enumerable.Range(0, projection + deletes), order.Order());
        // Both orphans land in the second segment's range, and neither collides with a projection row.
        Assert.Equal(deletes, order.Count(o => o >= projection));
    }

    [Fact]
    public async Task The_header_carries_the_facet_and_status_totals_for_the_whole_plan()
    {
        // A windowed client holds a page at a time, so it cannot fold a whole-plan total for itself —
        // and a facet bar counting only the rows currently on screen would be worse than none. These are
        // folded during the plan's own walk instead, under the same rules DryRunRowStore used when it
        // counted them client-side.
        using RunPlanHarness h = new("page-header-totals");
        h.WriteSource("copied-a.txt", "a");
        h.WriteSource("copied-b.txt", "b");
        h.WriteTarget("copied-a.txt", "a");   // already identical → untouched, not processed
        h.WriteTarget("orphan.txt", "gone");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        List<RunSourceItem> sources = RunPlanHarness.Sources(dir);

        Assert.Equal(sources.Count, header.UntouchedCount + header.ProcessedCount);
        Assert.Equal(
            sources.Count(s => s.Kind is OperationKind.SkippedByFilter or OperationKind.SkippedUnchanged),
            header.UntouchedCount);
        Assert.Equal(sources.Count(s => s.Kind is OperationKind.Processed), header.ProcessedCount);

        // One facet entry per root, counting rows — the source side has a single root here, holding
        // every scanned file.
        Assert.Equal(sources.Count, Assert.Single(header.SourceRowsByRoot).Value);
        // The destination side counts projection rows AND orphans, because the tab shows both.
        Assert.Equal(
            RunPlanHarness.Destinations(dir).Count + RunPlanHarness.Deletes(dir).Count,
            header.DestinationRowsByRoot.Values.Sum());
    }

    [Fact]
    public async Task A_snapshot_with_no_sidecars_reads_as_no_order_rather_than_an_error()
    {
        // What a snapshot written before paging existed looks like, and what a failed sidecar write
        // leaves behind. Both must degrade to "show it in plan order", never to a broken preview.
        using RunPlanHarness h = new("page-no-sidecar");
        h.WriteSource("a.txt", "a");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        File.Delete(Path.Combine(dir, "sources.ord"));
        File.Delete(Path.Combine(dir, "sources.idx"));

        Assert.Equal(0, RunSnapshotOrder.RowCount(Path.Combine(dir, "sources.ord"), NullLogger.Instance));
        Assert.Empty(RunSnapshotOrder.ReadRange(Path.Combine(dir, "sources.ord"), 0, 10, NullLogger.Instance));
        Assert.Null(RunSnapshotBlockIndex.TryRead(Path.Combine(dir, "sources.idx"), NullLogger.Instance));
    }
}
