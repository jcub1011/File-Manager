using System.Runtime.CompilerServices;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.UI.ViewModels;
using Xunit.Abstractions;

namespace FileManager.UI.Tests;

/// <summary>Measures what <see cref="DryRunViewModel.ApplyReport"/> leaves on the retained heap at
/// the engine's <c>MaxStreamedFiles</c> cap (500k source files) — the number a populated dry-run
/// preview holds for its lifetime. BenchmarkDotNet's Allocated column is per-op allocation, not
/// retention, so this probe is the gauge for the memory-optimization work. Filter with
/// <c>dotnet test --filter Category=Memory</c>; it builds a 500k-file report and is slow.</summary>
public sealed class DryRunViewModelMemoryTests(ITestOutputHelper output)
{
    private const int FileCount = 500_000;

    [Fact]
    [Trait("Category", "Memory")]
    public void ApplyReport_retained_heap_at_streamed_cap()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        (DryRunViewModel vm, WeakReference report) = BuildAndApply();
        long after = GC.GetTotalMemory(forceFullCollection: true);

        // The delta is meaningless unless the report itself was collected — otherwise it counts
        // report + rows instead of the rows the preview actually retains.
        Assert.False(report.IsAlive);

        long retained = after - before;
        output.WriteLine($"Retained after ApplyReport({FileCount:N0} files): {retained:N0} bytes ({retained / (1024.0 * 1024.0):F1} MB)");

        // Budget guard. History at this shape/count: 372 MB before any optimization; 250 MB after
        // lazy display strings + root interning. The margin catches regressions back toward eager
        // per-row strings without flaking on GC noise.
        Assert.True(retained < 300L * 1024 * 1024,
            $"ApplyReport retained {retained / (1024.0 * 1024.0):F1} MB — over the 300 MB budget");

        GC.KeepAlive(vm);   // rows must outlive the second measurement
    }

    /// <summary>NoInlining so the report reference provably dies with this frame — nulling a local
    /// in the caller would not guarantee the JIT treats it as unreachable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunViewModel Vm, WeakReference Report) BuildAndApply()
    {
        DryRunReport report = BuildReport(FileCount);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyReport(report);
        return (vm, new WeakReference(report));
    }

    /// <summary>Same report shape as <c>DryRunViewModelBenchmarks.BuildReport</c> so the two gauges
    /// measure the same workload: files spread across the three source dispositions, processed rows
    /// fanning out to two targets with a mix of operation kinds.</summary>
    private static DryRunReport BuildReport(int fileCount)
    {
        var sourceFiles = new List<PhysicalFile>(fileCount);
        var sourceOps = new List<VirtualFileOperation>(fileCount);
        var destinationFiles = new List<PhysicalFile>();
        var destinationOps = new List<VirtualFileOperation>();

        for (int i = 0; i < fileCount; i++)
        {
            string source = $@"C:\src\dir-{i % 64}\file-{i}.dat";
            sourceFiles.Add(new PhysicalFile
            {
                Path = source,
                Root = @"C:\src",
                Length = i,
                LastWritten = DateTimeOffset.UnixEpoch,
            });

            switch (i % 3)
            {
                case 0:
                    sourceOps.Add(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.Processed,
                        SourceIndex = i,
                        SourceDisposition = i % 6 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource,
                    });

                    string firstTarget = $@"C:\dst\file-{i}.dat";
                    if (i % 4 == 0)
                    {
                        int subject = destinationFiles.Count;
                        destinationFiles.Add(new PhysicalFile { Path = firstTarget, Root = @"C:\dst", Length = i, LastWritten = DateTimeOffset.UnixEpoch });
                        destinationOps.Add(new VirtualFileOperation { Path = firstTarget, Root = @"C:\dst", Kind = OperationKind.Overwrite, SourceIndex = i, SubjectIndex = subject, Detail = "existing file" });
                    }
                    else
                    {
                        destinationOps.Add(new VirtualFileOperation { Path = firstTarget, Root = @"C:\dst", Kind = OperationKind.New, SourceIndex = i });
                    }

                    if (i % 5 == 0)
                    {
                        string original = $@"C:\dst2\file-{i}.dat";
                        int subject = destinationFiles.Count;
                        destinationFiles.Add(new PhysicalFile { Path = original, Root = @"C:\dst2", Length = i, LastWritten = DateTimeOffset.UnixEpoch });
                        destinationOps.Add(new VirtualFileOperation { Path = $@"C:\dst2\file-{i} (1).dat", Root = @"C:\dst2", Kind = OperationKind.Rename, SourceIndex = i, Detail = "renamed to avoid a conflict" });
                        destinationOps.Add(new VirtualFileOperation { Path = original, Root = @"C:\dst2", Kind = OperationKind.Untouched, SubjectIndex = subject, Detail = "kept" });
                    }
                    else
                    {
                        destinationOps.Add(new VirtualFileOperation { Path = $@"C:\dst2\file-{i}.dat", Root = @"C:\dst2", Kind = OperationKind.New, SourceIndex = i });
                    }
                    break;

                case 1:
                    sourceOps.Add(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.SkippedByFilter,
                        SourceIndex = i,
                        Detail = "exclude *.tmp",
                    });
                    break;

                default:
                    sourceOps.Add(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\src",
                        Kind = OperationKind.SkippedUnchanged,
                        SourceIndex = i,
                    });
                    string unchanged = $@"C:\dst\file-{i}.dat";
                    int unchangedSubject = destinationFiles.Count;
                    destinationFiles.Add(new PhysicalFile { Path = unchanged, Root = @"C:\dst", Length = i, LastWritten = DateTimeOffset.UnixEpoch });
                    destinationOps.Add(new VirtualFileOperation { Path = unchanged, Root = @"C:\dst", Kind = OperationKind.SkipUnchanged, SourceIndex = i, SubjectIndex = unchangedSubject, Detail = "identical content (SHA-256)" });
                    break;
            }
        }

        return new DryRunReport
        {
            ProfileId = Guid.NewGuid(),
            GeneratedAt = DateTimeOffset.UtcNow,
            SourceFiles = sourceFiles,
            DestinationFiles = destinationFiles,
            SourceOperations = sourceOps,
            DestinationOperations = destinationOps,
        };
    }
}
