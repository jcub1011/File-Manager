using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Benchmarks.ViewModels;

/// <summary>Measures <see cref="DryRunViewModel.ApplyReport"/> — the UI-thread aggregation that runs
/// once per dry run: a projection over every report row (with a nested projection per target), a set of
/// O(n) LINQ count passes (per-disposition counts plus Overwrite/Rename/Disposal, each with a nested
/// per-target count), then a single merged <c>RebuildVisibleRows</c> pipeline that filters every row by
/// disposition/destructive/source/search and sorts the survivors by path into one <c>AffectedFiles</c>
/// list (no longer three-list bucketing). The report is built once in setup; the measured call is pure
/// aggregation. The gateway/folder-picker are never touched by ApplyReport, so null suffices — this
/// isolates the aggregation from IPC.</summary>
[MemoryDiagnoser]
public class DryRunViewModelBenchmarks
{
    private DryRunViewModel _viewModel = null!;
    private DryRunReport _report = null!;

    /// <summary>Report rows to aggregate; 50k is the engine's <c>MaxReportedFiles</c> cap.</summary>
    [Params(1_000, 10_000, 50_000)]
    public int FileCount { get; set; }

    /// <summary>Exercises both branches of the merged <c>RebuildVisibleRows</c> pipeline: false keeps
    /// every disposition ("Would process" + Filtered + Unchanged); true switches the processed view to
    /// the destructive-only subset (filtered/unchanged rows are untouched).</summary>
    [Params(false, true)]
    public bool DestructiveOnly { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _viewModel = new DryRunViewModel(gateway: null!)
        {
            ShowDestructive = DestructiveOnly,
        };
        _report = BuildReport(FileCount);
    }

    [Benchmark]
    public void ApplyReport() => _viewModel.ApplyReport(_report);

    /// <summary>Spreads files across the three dispositions with a mix of target kinds so every
    /// LINQ pass in ApplyReport does real work: WouldProcess rows carry two targets (one an overwrite
    /// every few rows) and a destructive source disposition, so Overwrite/Rename/Disposal counts and
    /// the destructive filter are all non-trivial.</summary>
    private static DryRunReport BuildReport(int fileCount)
    {
        var files = new List<DryRunFileResult>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            string source = $@"C:\src\dir-{i % 64}\file-{i}.dat";
            switch (i % 3)
            {
                case 0:   // WouldProcess with targets — the rows the count passes iterate
                    files.Add(new DryRunFileResult
                    {
                        SourcePath = source,
                        Disposition = DryRunFileDisposition.WouldProcess,
                        SourceDisposition = i % 6 == 0 ? "MoveToTrash" : "KeepSource",
                        Targets =
                        [
                            new DryRunTargetAction
                            {
                                TargetPath = $@"C:\dst\file-{i}.dat",
                                Kind = i % 4 == 0 ? DryRunTargetKind.WouldOverwrite : DryRunTargetKind.WouldWrite,
                                Detail = "existing file",
                            },
                            new DryRunTargetAction
                            {
                                TargetPath = $@"C:\dst2\file-{i}.dat",
                                Kind = i % 5 == 0 ? DryRunTargetKind.WouldRenameTo : DryRunTargetKind.WouldWrite,
                                Detail = i % 5 == 0 ? $@"C:\dst2\file-{i} (1).dat" : null,
                            },
                        ],
                    });
                    break;
                case 1:
                    files.Add(new DryRunFileResult
                    {
                        SourcePath = source,
                        Disposition = DryRunFileDisposition.WouldSkipFilter,
                        DecidingFilter = "exclude *.tmp",
                    });
                    break;
                default:
                    files.Add(new DryRunFileResult
                    {
                        SourcePath = source,
                        Disposition = DryRunFileDisposition.WouldSkipUnchanged,
                        Targets =
                        [
                            new DryRunTargetAction
                            {
                                TargetPath = $@"C:\dst\file-{i}.dat",
                                Kind = DryRunTargetKind.WouldSkipUnchanged,
                                Detail = "identical content (SHA-256)",
                            },
                        ],
                    });
                    break;
            }
        }

        return new DryRunReport(Guid.NewGuid(), DateTimeOffset.UtcNow, files, Truncated: false);
    }
}
