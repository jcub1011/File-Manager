using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Primitives;
using FileManager.Core.Benchmarks.Fixtures;
using FileManager.Core.Files;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.Files;

/// <summary>Single-level enumeration cost per entry — the innermost hot path of every scan, and the
/// place two separate claims land.
///
/// <para><b>Claim 1 (commit 706136e):</b> <c>FileSystemService</c> reads straight from the OS
/// find-data through a <c>FileSystemEnumerator&lt;T&gt;</c> subclass — "zero allocation per entry (no
/// FileInfo/DirectoryInfo objects), which is the point of this shape". The baseline tier is the
/// <c>DirectoryInfo.EnumerateFileSystemInfos()</c> shape the current <c>EnumerationOptions</c> were
/// explicitly chosen to stay compatible with, so the two enumerate the same set.</para>
///
/// <para><b>Claim 2 (Stage 4):</b> entries are built as (directory, name) pairs sharing one directory
/// string per enumerated directory, with <c>FullPath</c> joined lazily — "the enumeration hot path
/// never pays a per-entry path string, which at a 500k-file destination sweep is ~100 MB of churn
/// nobody reads". <see cref="EnumerateEntriesPairOnly"/> is the sweep's real access pattern;
/// <see cref="EnumerateEntriesWithFullPath"/> reads <c>FullPath</c> on every entry. <b>The delta
/// between those two tiers is the claim</b> — divide it by <see cref="FilesPerDirectory"/> and expect
/// roughly one ~90-character string (~200 B) per entry.</para>
///
/// <para>One flat directory, because a single level IS the unit of work here: the enumerator never
/// recurses and callers own their own traversal. Directory-tree traversal cost belongs to
/// <c>ScanSchedulerBenchmarks</c> and <c>SourceScannerBenchmarks</c>.</para></summary>
[MemoryDiagnoser]
public class FileSystemEnumerationBenchmarks
{
    private FileSystemService _fs = null!;
    private string _root = null!;
    private string _directory = null!;

    [Params(1_000, 10_000)]
    public int FilesPerDirectory { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-bench-enum-" + Guid.NewGuid().ToString("N"));
        _directory = Path.Combine(_root, "flat");
        DiskTree.BuildFlat(_directory, FilesPerDirectory);
        _fs = new FileSystemService(NullLogger<FileSystemService>.Instance);
    }

    [GlobalCleanup]
    public void Cleanup() => DiskTree.Delete(_root);

    /// <summary>The pre-optimization shape: one <c>FileInfo</c>/<c>DirectoryInfo</c> object per entry,
    /// each carrying its own full path string.</summary>
    [Benchmark(Baseline = true)]
    public long DirectoryInfoEnumerate()
    {
        long bytes = 0;
        foreach (FileSystemInfo info in new DirectoryInfo(_directory).EnumerateFileSystemInfos())
        {
            // Every field the scanner populates, including BOTH timestamps. Reading them here is not
            // padding: FileSystemEntry is built eagerly with Modified (converted to local) and
            // Created, so a baseline that skipped them would charge the shipping path for work it
            // was never compared against and turn a fair tie into a fake regression.
            bytes += info.Name.Length + (int)info.Attributes
                + info.LastWriteTime.Ticks + info.CreationTimeUtc.Ticks;
            if (info is FileInfo file)
                bytes += file.Length;
        }
        return bytes;
    }

    /// <summary>The shipping path, accessed the way the destination sweep accesses it: name, size,
    /// attributes and timestamps, all served from the find-data snapshot with no extra stat.
    /// <c>FullPath</c> is never touched, so no entry ever joins its path.</summary>
    [Benchmark]
    public long EnumerateEntriesPairOnly()
    {
        long bytes = 0;
        foreach (Result<FileSystemEntry, EnumerationFault> result in _fs.EnumerateEntries(_directory))
        {
            if (!result.TryGetValue(out FileSystemEntry? entry))
                continue;
            bytes += entry.FileName.Length + entry.Size + (int)entry.Attributes
                + entry.Modified.Ticks + entry.Created.Ticks;
        }
        return bytes;
    }

    /// <summary>The same enumeration with one added read of <c>FullPath</c> per entry, which forces
    /// the lazy join. Everything else is identical, so the difference from
    /// <see cref="EnumerateEntriesPairOnly"/> is exactly the per-entry path string Stage 4
    /// removed.</summary>
    [Benchmark]
    public long EnumerateEntriesWithFullPath()
    {
        long bytes = 0;
        foreach (Result<FileSystemEntry, EnumerationFault> result in _fs.EnumerateEntries(_directory))
        {
            if (!result.TryGetValue(out FileSystemEntry? entry))
                continue;
            bytes += entry.FileName.Length + entry.Size + (int)entry.Attributes
                + entry.Modified.Ticks + entry.Created.Ticks
                + entry.FullPath.Length;
        }
        return bytes;
    }
}
