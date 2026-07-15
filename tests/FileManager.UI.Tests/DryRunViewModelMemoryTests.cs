using System.Diagnostics;
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
/// <remarks>Pinned to a non-parallel collection: <see cref="GC.GetTotalMemory(bool)"/> measures the
/// whole process heap, so letting other collections allocate concurrently during a full-suite run
/// would contaminate the before/after delta and flake the budget asserts.</remarks>
[Collection("Memory")]
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
        // lazy display strings + root interning; 238 MB after the directory-table report (this
        // shallow shape's ~28-char paths understate that step — see the deep-path probe below).
        // The margin catches regressions back toward eager per-row strings without flaking on GC noise.
        Assert.True(retained < 280L * 1024 * 1024,
            $"ApplyReport retained {retained / (1024.0 * 1024.0):F1} MB — over the 280 MB budget");

        GC.KeepAlive(vm);   // rows must outlive the second measurement
    }

    /// <summary>The benchmark shape above is unrealistically shallow (64 directories, ~28-char
    /// paths), which understates what the directory-table report buys: there a filename is nearly
    /// as long as the whole path. This variant uses a realistic tree — ~25k directories, ~100-char
    /// paths — where per-row path bytes dominated the old flat-string model. History at this shape:
    /// 519 MB before any optimization, 307 MB after lazy display strings + interned roots, 219 MB
    /// with the directory-table report — whose property this budget pins: retained heap no longer
    /// scales with path depth.</summary>
    [Fact]
    [Trait("Category", "Memory")]
    public void ApplyReport_retained_heap_at_streamed_cap_with_realistic_deep_paths()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        (DryRunViewModel vm, WeakReference report) = BuildAndApplyDeep();
        long after = GC.GetTotalMemory(forceFullCollection: true);

        Assert.False(report.IsAlive);

        long retained = after - before;
        output.WriteLine($"Retained after ApplyReport({FileCount:N0} deep-path files): {retained:N0} bytes ({retained / (1024.0 * 1024.0):F1} MB)");

        Assert.True(retained < 260L * 1024 * 1024,
            $"ApplyReport retained {retained / (1024.0 * 1024.0):F1} MB — over the 260 MB budget");

        GC.KeepAlive(vm);
    }

    /// <summary>What turning on tree view adds on top of the populated preview — the forest of
    /// <see cref="DryRunTreeNode"/>s plus the TreeDataGrid sources — and how long the synchronous
    /// build blocks for, at the streamed cap on the realistic deep-path shape. The build runs on
    /// the UI thread, so the wall time here is the freeze the user feels.</summary>
    [Fact]
    [Trait("Category", "Memory")]
    public void Tree_toggle_retained_heap_and_build_time_at_streamed_cap_with_realistic_deep_paths()
    {
        (DryRunViewModel vm, WeakReference report) = BuildAndApplyDeep();

        long before = GC.GetTotalMemory(forceFullCollection: true);
        Assert.False(report.IsAlive);

        Stopwatch watch = Stopwatch.StartNew();
        vm.Sources.ShowTree = true;
        long sourcesMs = watch.ElapsedMilliseconds;
        vm.Destinations.ShowTree = true;
        watch.Stop();

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long retained = after - before;
        output.WriteLine(
            $"Tree toggle (both tabs, {FileCount:N0} deep-path files): retained {retained:N0} bytes " +
            $"({retained / (1024.0 * 1024.0):F1} MB); Sources build {sourcesMs:N0} ms, both tabs {watch.ElapsedMilliseconds:N0} ms");

        // Budget guard. History at this shape/count: 681 MB / ~4.8 s when BuildForest split every
        // row's absolute path (one node per file, each retaining its own full path, counts
        // dictionary, and pill strings); 116 MB / ~1.6 s building from the shared directory
        // structure (leaves reference their rows' strings; pills memoized). Time is reported but
        // not asserted — wall clock flakes across machines.
        Assert.True(retained < 150L * 1024 * 1024,
            $"Tree toggle retained {retained / (1024.0 * 1024.0):F1} MB — over the 150 MB budget");

        GC.KeepAlive(vm);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DryRunViewModel Vm, WeakReference Report) BuildAndApplyDeep()
    {
        DryRunReport report = BuildDeepReport(FileCount);
        DryRunViewModel vm = new(gateway: null!);
        vm.ApplyReport(report);
        return (vm, new WeakReference(report));
    }

    /// <summary>A realistic 500k-file tree: 20 files per leaf directory, leaves nested four levels
    /// under the root (project/assets/renders/batch), absolute paths ~100 chars. Ops mirror the
    /// benchmark shape's disposition mix (processed with two targets / filtered / unchanged).</summary>
    private static DryRunReport BuildDeepReport(int fileCount)
    {
        DryRunDirectoryTableBuilder dirs = new();
        var sourceFiles = new List<DryRunFile>(fileCount);
        var sourceOps = new List<DryRunOperation>(fileCount);
        var destinationFiles = new List<DryRunFile>();
        var destinationOps = new List<DryRunOperation>();

        for (int i = 0; i < fileCount; i++)
        {
            int leaf = i / 20;                    // 20 files per directory → 25k leaf dirs at 500k
            string sourceDir = $@"C:\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}";
            string source = $@"{sourceDir}\render-output-{i:D7}.png";
            sourceFiles.Add(dirs.Convert(new PhysicalFile
            {
                Path = source,
                Root = @"C:\media-archive\projects",
                Length = i,
                LastWritten = DateTimeOffset.UnixEpoch,
            }));

            switch (i % 3)
            {
                case 0:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\media-archive\projects",
                        Kind = OperationKind.Processed,
                        SourceIndex = i,
                        SourceDisposition = i % 6 == 0 ? OnSuccessAction.MoveToTrash : OnSuccessAction.KeepSource,
                    }));
                    string target = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\render-output-{i:D7}.png";
                    destinationOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = target, Root = @"D:\backup\media-archive\projects", Kind = OperationKind.New, SourceIndex = i,
                    }));
                    break;

                case 1:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\media-archive\projects",
                        Kind = OperationKind.SkippedByFilter,
                        SourceIndex = i,
                        Detail = "exclude *.tmp",
                    }));
                    break;

                default:
                    sourceOps.Add(dirs.Convert(new VirtualFileOperation
                    {
                        Path = source,
                        Root = @"C:\media-archive\projects",
                        Kind = OperationKind.SkippedUnchanged,
                        SourceIndex = i,
                    }));
                    string unchanged = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\render-output-{i:D7}.png";
                    int unchangedSubject = destinationFiles.Count;
                    destinationFiles.Add(dirs.Convert(new PhysicalFile { Path = unchanged, Root = @"D:\backup\media-archive\projects", Length = i, LastWritten = DateTimeOffset.UnixEpoch }));
                    destinationOps.Add(dirs.Convert(new VirtualFileOperation { Path = unchanged, Root = @"D:\backup\media-archive\projects", Kind = OperationKind.SkipUnchanged, SourceIndex = i, SubjectIndex = unchangedSubject, Detail = "identical content (SHA-256)" }));
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

    /// <summary>Same report shape as <c>DryRunViewModelBenchmarks.BuildReport</c> so the two gauges
    /// measure the same workload: files spread across the three source dispositions, processed rows
    /// fanning out to two targets with a mix of operation kinds.</summary>
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
                case 0:
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
