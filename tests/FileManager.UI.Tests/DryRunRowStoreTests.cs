using System.Collections;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>The invariants the columnar row store and its handle rows rest on. These are not
/// behavioural assertions about the preview (<c>DryRunViewModelTests</c> covers that) — they pin the
/// three properties that, if they broke, would silently undo the memory work or the row identity it
/// depends on.</summary>
public sealed class DryRunRowStoreTests
{
    private readonly DryRunDirectoryTableBuilder _dirs = new();

    private DryRunFile Pf(string path, string root, long length = 0) =>
        _dirs.Convert(new PhysicalFile { Path = path, Root = root, Length = length, LastWritten = DateTimeOffset.UnixEpoch });

    private DryRunOperation Op(string path, string root, OperationKind kind,
        int sourceIndex = -1, int subjectIndex = -1, OnSuccessAction? disposition = null, string? detail = null) =>
        _dirs.Convert(new VirtualFileOperation
        {
            Path = path, Root = root, Kind = kind, SourceIndex = sourceIndex,
            SubjectIndex = subjectIndex, SourceDisposition = disposition, Detail = detail,
        });

    private DryRunChunkResponse Chunk(
        IReadOnlyList<DryRunDirectory> directories,
        IReadOnlyList<DryRunFile> sourceFiles,
        IReadOnlyList<DryRunOperation> sourceOps,
        IReadOnlyList<DryRunFile> destinationFiles,
        IReadOnlyList<DryRunOperation> destinationOps) => new()
        {
            Directories = directories,
            SourceFiles = sourceFiles,
            SourceOperations = sourceOps,
            DestinationFiles = destinationFiles,
            DestinationOperations = destinationOps,
        };

    // ── Ingest ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Chunked_ingest_matches_ingesting_the_same_records_as_one_chunk()
    {
        // Two source files with a target each, delivered as two frames — the split every real run
        // arrives in. Indices are global, so file 1's operation carries SourceIndex 1 even though it
        // is the first record of its own chunk: that is the property this pins.
        DryRunFile a = Pf(@"C:\s\a.txt", @"C:\s", 10);
        DryRunOperation aSrc = Op(@"C:\s\a.txt", @"C:\s", OperationKind.Processed, sourceIndex: 0, disposition: OnSuccessAction.KeepSource);
        DryRunOperation aDst = Op(@"C:\t\a.txt", @"C:\t", OperationKind.New, sourceIndex: 0);
        DryRunFile b = Pf(@"C:\s\b.txt", @"C:\s", 20);
        DryRunOperation bSrc = Op(@"C:\s\b.txt", @"C:\s", OperationKind.Processed, sourceIndex: 1, disposition: OnSuccessAction.MoveToTrash);
        DryRunOperation bDst = Op(@"C:\t\b.txt", @"C:\t", OperationKind.New, sourceIndex: 1);
        List<DryRunDirectory> table = [.. _dirs.Entries];

        DryRunRowStore chunked = DryRunRowStore.CreateForIngest();
        chunked.OnChunk(Chunk(table, [a], [aSrc], [], [aDst]));
        chunked.OnChunk(Chunk([], [b], [bSrc], [], [bDst]));
        chunked.Complete();

        DryRunRowStore single = DryRunRowStore.FromReport(new DryRunReport
        {
            ProfileId = Guid.Empty,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = table,
            SourceFiles = [a, b],
            DestinationFiles = [],
            SourceOperations = [aSrc, bSrc],
            DestinationOperations = [aDst, bDst],
        });

        Assert.Equal(single.SourceCount, chunked.SourceCount);
        Assert.Equal(single.OperationCount, chunked.OperationCount);
        for (int i = 0; i < single.SourceCount; i++)
        {
            Assert.Equal(single.SourceDirPath(i), chunked.SourceDirPath(i));
            Assert.Equal(single.SourceFileName(i), chunked.SourceFileName(i));
            Assert.Equal(single.SourceSize(i), chunked.SourceSize(i));
            Assert.Equal(single.SourceKind(i), chunked.SourceKind(i));
            Assert.Equal(single.SourceDispositionText(i), chunked.SourceDispositionText(i));
            Assert.Equal(single.TargetStart(i), chunked.TargetStart(i));
            Assert.Equal(single.TargetEnd(i), chunked.TargetEnd(i));
        }
        for (int p = 0; p < single.OperationCount; p++)
        {
            Assert.Equal(single.OpDirPath(p), chunked.OpDirPath(p));
            Assert.Equal(single.OpFileName(p), chunked.OpFileName(p));
            Assert.Equal(single.OpKind(p), chunked.OpKind(p));
            Assert.Equal(single.OpSize(p), chunked.OpSize(p));
        }
        Assert.Equal(single.DisposalCount, chunked.DisposalCount);
    }

    [Fact]
    public void A_source_operation_that_arrives_before_its_file_still_lands()
    {
        // The engine emits whole per-file bundles, so this should not happen — but the batch
        // projection this replaced read the assembled report and was order-independent by
        // construction. Dropping such an operation would silently show the row as an un-annotated
        // Processed rather than the skip it is, so ingest defers it instead.
        DryRunFile file = Pf(@"C:\s\late.tmp", @"C:\s");
        DryRunOperation op = Op(@"C:\s\late.tmp", @"C:\s", OperationKind.SkippedByFilter,
            sourceIndex: 0, detail: "exclude *.tmp");
        List<DryRunDirectory> table = [.. _dirs.Entries];

        DryRunRowStore store = DryRunRowStore.CreateForIngest();
        store.OnChunk(Chunk(table, [], [op], [], []));   // operation first, file nowhere yet
        store.OnChunk(Chunk([], [file], [], [], []));
        store.Complete();

        Assert.Equal(OperationKind.SkippedByFilter, store.SourceKind(0));
        Assert.Equal("exclude *.tmp", store.SourceDetail(0));
    }

    [Fact]
    public void An_operations_size_falls_back_to_the_destination_file_it_touches()
    {
        // A Deleted orphan has no incoming content, so its size is the pre-existing file's — the
        // reason destination file lengths are ingested at all.
        DryRunFile existing = Pf(@"C:\t\orphan.txt", @"C:\t", 4096);
        DryRunOperation deleted = Op(@"C:\t\orphan.txt", @"C:\t", OperationKind.Deleted, subjectIndex: 0);
        DryRunRowStore store = DryRunRowStore.CreateForIngest();
        store.OnChunk(Chunk([.. _dirs.Entries], [], [], [existing], [deleted]));
        store.Complete();

        Assert.Equal(1, store.OperationCount);
        Assert.Equal(0, store.NoSourceStart);        // no owning source — its own single-entry row
        Assert.Equal(4096, store.OpSize(0));
    }

    [Fact]
    public void A_files_name_is_interned_across_records()
    {
        // A copy preserves the name, so the destination operation's FileName arrives as a separate
        // instance holding identical text. Sharing one instance is a large slice of the retained
        // saving at the cap, so it is pinned by reference, not by value.
        DryRunFile file = Pf(@"C:\s\report.txt", @"C:\s");
        DryRunOperation src = Op(@"C:\s\report.txt", @"C:\s", OperationKind.Processed, sourceIndex: 0);
        DryRunOperation dst = Op(@"C:\t\report.txt", @"C:\t", OperationKind.New, sourceIndex: 0);
        // Force distinct instances, exactly as JSON deserialization produces them.
        dst.FileName = new string("report.txt".ToCharArray());
        Assert.NotSame(file.FileName, dst.FileName);

        DryRunRowStore store = DryRunRowStore.CreateForIngest();
        store.OnChunk(Chunk([.. _dirs.Entries], [file], [src], [], [dst]));
        store.Complete();

        Assert.Same(store.SourceFileName(0), store.OpFileName(0));
    }

    // ── Row identity ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_handles_for_the_same_row_are_equal()
    {
        // Load-bearing: the bound lists build a handle per indexer access, so ListBox selection,
        // IndexOf and container recycling all depend on handles being values rather than identities.
        // The previous row model was rejected for a lazy projection precisely because it compared its
        // nested target list by reference (docs/dry-run-memory-optimization.md, Optimization 3).
        DryRunRowStore store = TwoFileStore();

        DryRunFileRow first = new(store, 1);
        DryRunFileRow second = new(store, 1);
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new DryRunFileRow(store, 0));

        // Actions are an init property, not a positional member, so they must stay out of equality —
        // rows carry them in the app and not in tests.
        Assert.Equal(first, second with { Actions = null });
    }

    [Fact]
    public void Destination_rows_with_different_entry_slices_are_not_equal()
    {
        // A filtered Destinations view can show a subset of a row's fan-out. Two views showing
        // different subsets of the same source are different rows and must not be conflated.
        DryRunRowStore store = TwoFileStore();
        int[] slice = [0, 1];

        DryRunDestinationRow whole = new(store, 0);
        DryRunDestinationRow sliced = new(store, 0, slice, 0, 1);
        Assert.NotEqual(whole, sliced);
        Assert.Equal(sliced, new DryRunDestinationRow(store, 0, slice, 0, 1));
    }

    // ── The bound list ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bound_row_list_is_an_IList()
    {
        // Load-bearing, and easy to lose by accident. Avalonia's ItemsSourceView uses IList for
        // indexed access and otherwise copies the whole source into a list — which would materialize
        // every handle up front and undo the point of the store. Before the columnar change this held
        // only because the bound instance happened to be a List<T>.
        DryRunSourcesTab tab = new(TimeSpan.Zero);
        tab.Load(TwoFileStore());

        Assert.IsAssignableFrom<IList>(tab.VisibleRows);
        IList list = (IList)tab.VisibleRows;
        Assert.Equal(2, list.Count);

        // IndexOf has to survive re-materialization: the row it is handed is a different instance
        // from the one the list would produce for that position.
        DryRunFileRow row = tab.VisibleRows[1];
        Assert.Equal(1, list.IndexOf(row));
        Assert.True(list.Contains(row));
        Assert.Equal(-1, list.IndexOf(new DryRunFileRow(DryRunRowStore.Empty, 0)));
    }

    [Fact]
    public void An_empty_store_answers_without_a_report()
    {
        // The tabs hold Empty before the first run and after a clear, so every accessor has to be
        // safe on it rather than the tabs null-checking a store on every row read.
        Assert.True(DryRunRowStore.Empty.IsCompleted);
        Assert.Equal(0, DryRunRowStore.Empty.SourceCount);
        Assert.Equal(0, DryRunRowStore.Empty.OperationCount);
        Assert.Null(DryRunRowStore.Empty.SourceCommonRoot);
    }

    /// <summary>Two source files, each with one destination operation.</summary>
    /// <remarks>Every record is converted into locals BEFORE the report is built: converting a path
    /// is what adds its directory to the table, so snapshotting <c>_dirs.Entries</c> inline would
    /// capture the table as of that member's position in the initializer and leave later records
    /// pointing past its end.</remarks>
    private DryRunRowStore TwoFileStore()
    {
        DryRunFile a = Pf(@"C:\s\a.txt", @"C:\s", 1);
        DryRunFile b = Pf(@"C:\s\b.txt", @"C:\s", 2);
        DryRunOperation aSrc = Op(@"C:\s\a.txt", @"C:\s", OperationKind.Processed, sourceIndex: 0, disposition: OnSuccessAction.KeepSource);
        DryRunOperation bSrc = Op(@"C:\s\b.txt", @"C:\s", OperationKind.Processed, sourceIndex: 1, disposition: OnSuccessAction.KeepSource);
        DryRunOperation aDst = Op(@"C:\t\a.txt", @"C:\t", OperationKind.New, sourceIndex: 0);
        DryRunOperation bDst = Op(@"C:\t\b.txt", @"C:\t", OperationKind.New, sourceIndex: 1);
        return DryRunRowStore.FromReport(new DryRunReport
        {
            ProfileId = Guid.Empty,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = [.. _dirs.Entries],
            SourceFiles = [a, b],
            DestinationFiles = [],
            SourceOperations = [aSrc, bSrc],
            DestinationOperations = [aDst, bDst],
        });
    }
}
