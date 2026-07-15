using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
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
    private DryRunReport _report = null!;

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
    }

    [Benchmark]
    public void ApplyReport() => _viewModel.ApplyReport(_report);

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
