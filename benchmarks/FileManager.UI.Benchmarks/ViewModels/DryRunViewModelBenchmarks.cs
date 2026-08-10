using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Benchmarks.ViewModels;

/// <summary>Measures what the preview actually pays per PAGE — folding one <c>DryRunChunkResponse</c>
/// into a <see cref="DryRunRowStore"/> and completing it — plus the cost of materializing the row
/// handles a viewport renders from it.
///
/// <para><b>What this replaced, and why.</b> It used to measure <c>ApplyReport</c> and
/// <c>PrepareReport</c>: the client ingesting an entire plan and sorting both halves of it, recorded at
/// 500,000 files as 838 ms / 149,709 KB and 557 ms / 71,783 KB. Neither call exists now. The service
/// sorts where the rows are and answers a window, so the client's per-plan cost is a header read and its
/// per-page cost is what follows — bounded by <see cref="PagedDryRunRowStore.PageRows"/> however large
/// the plan is, which is the whole point and also why there is no longer a <c>FileCount</c>
/// parameter.</para>
///
/// <para>The gateway is never touched here, so null suffices: this isolates the fold from IPC.</para></summary>
[MemoryDiagnoser]
public class DryRunViewModelBenchmarks
{
    private DryRunChunkResponse _sourcePage = null!;
    private DryRunChunkResponse _destinationPage = null!;
    private DryRunRowStore _residentPage = null!;

    /// <summary>Rows per page. The service's block size is the only value the app ever uses; the smaller
    /// points are there to show the fold is linear in the page and carries no fixed cliff.</summary>
    [Params(64, 512)]
    public int PageRows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sourcePage = BuildSourcePage(PageRows);
        _destinationPage = BuildDestinationPage(PageRows);
        _residentPage = Fold(_sourcePage);
    }

    /// <summary>One source page folded and completed — what the app pays each time a fetch lands while
    /// the user scrolls, and the figure that has to stay flat as plans grow.</summary>
    [Benchmark]
    public object FoldSourcePage() => Fold(_sourcePage);

    /// <summary>The destination half, whose rows are all no-source ops — a different path through
    /// <c>Complete</c>'s CSR grouping (nothing groups), so worth its own number.</summary>
    [Benchmark]
    public object FoldDestinationPage() => Fold(_destinationPage);

    /// <summary>Reading every row of a resident page the way the bound list does: a handle per indexer
    /// access, and the display strings a template pulls off it. This is the per-frame cost of scrolling,
    /// and it is what the lazy display-string design exists to keep small.</summary>
    [Benchmark]
    public int RealizeResidentRows()
    {
        int length = 0;
        for (int i = 0; i < _residentPage.SourceCount; i++)
        {
            DryRunFileRow row = new(_residentPage, i);
            length += row.FileName.Length + row.ParentDisplay.Length + row.SizeText.Length;
        }
        return length;
    }

    private static DryRunRowStore Fold(DryRunChunkResponse page)
    {
        DryRunRowStore store = DryRunRowStore.CreateForIngest();
        store.OnChunk(page);
        store.Complete();
        return store;
    }

    /// <summary>A page in the shape <c>GetRunPlanPageHandler.SourcePage</c> emits: one file and one
    /// operation per row, the operation naming its file by PAGE-LOCAL index, and each carrying the mask
    /// of kinds its destinations take. Realistic deep paths (~100 chars, 20 files per leaf directory), so
    /// the directory-table fold does the work a real page makes it do.</summary>
    private static DryRunChunkResponse BuildSourcePage(int rows)
    {
        DryRunDirectoryTableBuilder dirs = new();
        List<DryRunFile> files = [];
        List<DryRunOperation> ops = [];
        for (int i = 0; i < rows; i++)
        {
            int leaf = i / 20;
            string path = $@"C:\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\render-output-{i:D7}.png";
            files.Add(dirs.Convert(new PhysicalFile
            {
                Path = path, Root = SourceRoot, Length = i, LastWritten = DateTimeOffset.UnixEpoch,
            }));
            DryRunOperation op = dirs.Convert(new VirtualFileOperation
            {
                Path = path,
                Root = SourceRoot,
                Kind = i % 3 == 0 ? OperationKind.Processed : OperationKind.SkippedUnchanged,
                SourceIndex = files.Count - 1,
                SubjectIndex = -1,
                SourceDisposition = i % 6 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource,
                Detail = i % 3 == 0 ? null : "identical content (SHA-256)",
            });
            op.TargetKinds = OperationKindMask.Bit(i % 4 == 0 ? OperationKind.Overwrite : OperationKind.New);
            ops.Add(op);
        }
        return DryRunColumns.ToChunk(dirs.Entries.ToList(), files, [], ops, []);
    }

    /// <summary>A page in the shape <c>GetRunPlanPageHandler.DestinationPage</c> emits: one operation per
    /// row, none of them naming a source, and a subject file for the ones that act on something already
    /// there.</summary>
    private static DryRunChunkResponse BuildDestinationPage(int rows)
    {
        DryRunDirectoryTableBuilder dirs = new();
        List<DryRunFile> files = [];
        List<DryRunOperation> ops = [];
        for (int i = 0; i < rows; i++)
        {
            int leaf = i / 20;
            string path = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\render-output-{i:D7}.png";
            int subject = -1;
            if (i % 4 == 0)
            {
                files.Add(dirs.Convert(new PhysicalFile
                {
                    Path = path, Root = TargetRoot, Length = i, LastWritten = DateTimeOffset.UnixEpoch,
                }));
                subject = files.Count - 1;
            }
            ops.Add(dirs.Convert(new VirtualFileOperation
            {
                Path = path,
                Root = TargetRoot,
                Kind = i % 4 == 0 ? OperationKind.Overwrite : OperationKind.New,
                SourceIndex = -1,
                SubjectIndex = subject,
                Detail = i % 4 == 0 ? "existing file" : null,
            }));
        }
        return DryRunColumns.ToChunk(dirs.Entries.ToList(), [], files, [], ops);
    }

    private const string SourceRoot = @"C:\media-archive\projects";
    private const string TargetRoot = @"D:\backup\media-archive\projects";
}
