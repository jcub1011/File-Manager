using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Benchmarks.ViewModels;

/// <summary>Measures <see cref="DryRunViewModel.ApplyReport"/> — the UI-thread aggregation that runs
/// once per dry run under the physical-files + operations model: it builds a <c>SourceIndex</c> lookup
/// over the destination operations, projects every source file into a row (pairing it with its source
/// operation and gathering its target rows from that lookup), projects every destination operation 1:1
/// into a destination row, runs a handful of O(n) count passes for the blast-radius banner, then hands
/// both row lists to the two tabs' <c>Load</c>. The report is built once in setup; the measured call is
/// pure aggregation. The gateway/folder-picker are never touched by ApplyReport, so null suffices —
/// this isolates the aggregation from IPC.</summary>
[MemoryDiagnoser]
public class DryRunViewModelBenchmarks
{
    private DryRunViewModel _viewModel = null!;
    private DryRunViewModel _populated = null!;
    private DryRunReport _report = null!;
    private DryRunRowStore _store = null!;
    private DryRunCompletion _completion = null!;

    /// <summary>Source files to aggregate; 500k is the engine's <c>MaxStreamedFiles</c> cap — the
    /// bound on the streamed path the UI actually uses (<c>MaxReportedFiles</c> only guards the
    /// legacy single-frame batched path).</summary>
    [Params(1_000, 10_000, 50_000, 500_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _viewModel = new DryRunViewModel(gateway: null!);
        _report = BuildReport(FileCount);
        _completion = new DryRunCompletion(_report.GeneratedAt, _report.Truncated, _report.Space);
        _store = DryRunRowStore.FromReport(_report);
        _populated = new DryRunViewModel(gateway: null!);
        _populated.ApplyReport(_report);
    }

    /// <summary>The whole cost of turning a finished run into a populated preview: fold the records
    /// into the columnar store, then sort and count both tabs. Directly comparable to the figure this
    /// replaced (500k: 1,139 ms / 462 MB allocated), when the same call projected the report into two
    /// full sets of row objects instead.</summary>
    [Benchmark]
    public void ApplyReport() => _viewModel.ApplyReport(_report);

    /// <summary>Ingest alone — what the app pays incrementally, one chunk at a time, while the run is
    /// still streaming. Splitting it out from <see cref="PrepareReport"/> is what shows whether a
    /// regression landed in the fold or in the sorts.</summary>
    [Benchmark]
    public object IngestReport() => DryRunRowStore.FromReport(_report);

    /// <summary>The post-ingest preparation alone — the sorts, counts and facets. This is what
    /// <c>RunAsync</c> still runs on the thread pool once the stream ends; everything before it has
    /// already happened frame by frame.</summary>
    [Benchmark]
    public object PrepareReport() => DryRunViewModel.PrepareReport(_store, _completion);   // object: the record is internal, benchmark methods must be public

    /// <summary>A status-chip select and its deselect on a populated preview — the coalesced
    /// rebuilds that used to freeze the UI. Both passes run per invocation so every invocation does
    /// identical work: one real filter pass plus the deselect's no-filter fast path (alternating a
    /// single toggle per invocation would blend a real and a near-free pass bimodally).</summary>
    [Benchmark]
    public async Task ToggleStatusFilter()
    {
        DryRunStatusFilter chip = _populated.Sources.StatusFilters[0];
        chip.IsSelected = true;
        await _populated.Sources.PendingRebuild;
        chip.IsSelected = false;
        await _populated.Sources.PendingRebuild;
    }

    /// <summary>Spreads files across the three source dispositions with a mix of destination operation
    /// kinds so every projection and count pass in ApplyReport does real work: processed rows fan out to
    /// two targets (one an overwrite/rename every few rows) and carry a destructive source disposition,
    /// so the Overwrite/Rename/Disposal counts and the SourceIndex lookup are all non-trivial. Every
    /// destination operation references its source file by index, exactly as the engine emits it.</summary>
    private static DryRunReport BuildReport(int fileCount)
    {
        DryRunDirectoryTableBuilder dirs = new();
        var sourceFiles = new List<DryRunFile>(fileCount);
        var sourceOps = new List<DryRunOperation>(fileCount);
        var destinationFiles = new List<DryRunFile>();
        var destinationOps = new List<DryRunOperation>();

        for (int i = 0; i < fileCount; i++)
        {
            string source = $@"C:\src\dir-{i % 64}\file-{i}.dat";
            sourceFiles.Add(dirs.Convert(new PhysicalFile
            {
                Path = source,
                Root = @"C:\src",
                Length = i,
                LastWritten = DateTimeOffset.UnixEpoch,
            }));

            switch (i % 3)
            {
                case 0:   // Processed with two targets — the rows the count passes iterate.
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.Processed,
                        SourceIndex = i,
                        SourceDisposition = i % 6 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource,
                    }));

                    string firstTarget = $@"C:\dst\file-{i}.dat";
                    if (i % 4 == 0)
                    {
                        int subject = destinationFiles.Count;
                        destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = firstTarget, Root = @"C:\dst", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = firstTarget, Root = @"C:\dst", Kind = OperationKind.Overwrite, SourceIndex = i, SubjectIndex = subject, Detail = "existing file" }));
                    }
                    else
                    {
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = firstTarget, Root = @"C:\dst", Kind = OperationKind.New, SourceIndex = i }));
                    }

                    if (i % 5 == 0)
                    {
                        // A conflict rename: the suffixed new file plus the kept-original Untouched op.
                        string original = $@"C:\dst2\file-{i}.dat";
                        int subject = destinationFiles.Count;
                        destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = original, Root = @"C:\dst2", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = $@"C:\dst2\file-{i} (1).dat", Root = @"C:\dst2", Kind = OperationKind.Rename, SourceIndex = i, Detail = "renamed to avoid a conflict" }));
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = original, Root = @"C:\dst2", Kind = OperationKind.Untouched, SubjectIndex = subject, Detail = "kept" }));
                    }
                    else
                    {
                        destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = $@"C:\dst2\file-{i}.dat", Root = @"C:\dst2", Kind = OperationKind.New, SourceIndex = i }));
                    }
                    break;

                case 1:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.SkippedByFilter,
                        SourceIndex = i,
                        Detail = "exclude *.tmp",
                    }));
                    break;

                default:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.SkippedUnchanged,
                        SourceIndex = i,
                    }));
                    string unchanged = $@"C:\dst\file-{i}.dat";
                    int unchangedSubject = destinationFiles.Count;
                    destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = unchanged, Root = @"C:\dst", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                    destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = unchanged, Root = @"C:\dst", Kind = OperationKind.SkipUnchanged, SourceIndex = i, SubjectIndex = unchangedSubject, Detail = "identical content (SHA-256)" }));
                    break;
            }
        }

        return new DryRunReport
        {
            ProfileId = Guid.NewGuid(),
            GeneratedAt = DateTimeOffset.UtcNow,
            Directories = dirs.Entries.ToList(),
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
        };
    }
}
