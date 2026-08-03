using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Benchmarks.Fixtures;
using FileManager.Core.Files;
using FileManager.Core.Scanning;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.Scanning;

/// <summary>The wall-time comparison the scan-thread reduction was supposed to ship with.
///
/// <para><c>ScanThreadResolver</c> lowered the auto scan-thread ceiling from
/// <c>ProcessorCount * 8</c> capped at 256 to <c>min(ProcessorCount * 4, 64)</c>, and the per-drive
/// cap from <c>ProcessorCount * 4</c> to <c>ProcessorCount * 2</c>, on the reasoning that
/// "concurrent-read returns are a property of the device, not the CPU" and that "on one fast local
/// volume this should be unmeasurable". docs/dry-run-service-memory.md §9 requires that change ship
/// "only alongside a wall-time comparison"; no such comparison exists in the repository, so the
/// claim has never been falsifiable. This makes it so: sweep the pin across the retired ceiling
/// (256), the current one (64), and below, and read whether the curve is flat.</para>
///
/// <para><see cref="Backing"/> is what makes the answer interpretable rather than merely a disk
/// benchmark. <see cref="Backing.Disk"/> is the honest end-to-end number on the machine at hand;
/// <see cref="Backing.InMemory"/> removes I/O entirely, leaving the scheduler's own coordination
/// cost — the lock, the per-volume DFS stacks, the bounded channel, the park/re-arm cycle. If disk
/// is flat while in-memory rises with thread count, the flatness is the device saturating (the claim)
/// rather than the scheduler being free.</para>
///
/// <para>Both <c>MaxScanThreads</c> and <c>PerDriveDefault</c> are pinned to the same value: on a
/// single-volume walk the per-drive cap is the binding constraint, so pinning only the global one
/// would leave the parameter with no effect and produce a flat line for the wrong reason.</para></summary>
[MemoryDiagnoser]
public class ScanSchedulerBenchmarks
{
    private const int DirectoryCount = 500;
    private const int FilesPerDirectory = 200;
    private const string VolumeKey = "bench";

    private IFileSystemService _fs = null!;
    private ScanScheduler _scheduler = null!;
    private ScanSessionOptions _options = null!;
    private string _root = null!;

    /// <summary>Pinned scan-thread budget. 256 is the retired auto ceiling, 64 the current one; the
    /// lower values show where the curve actually starts to bend.</summary>
    [Params(1, 8, 32, 64, 256)]
    public int MaxScanThreads { get; set; }

    [Params(Backing.Disk, Backing.InMemory)]
    public Backing Source { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-bench-sched-" + Guid.NewGuid().ToString("N"));
        if (Source == Backing.Disk)
        {
            DiskTree.Build(_root, DirectoryCount * FilesPerDirectory, FilesPerDirectory);
            _fs = new FileSystemService(NullLogger<FileSystemService>.Instance);
        }
        else
        {
            _fs = new InMemoryFileSystem(_root, DirectoryCount, FilesPerDirectory);
        }

        ScanThreadingSettings threading = new()
        {
            MaxScanThreads = ThreadBudget.Explicit(MaxScanThreads),
            PerDriveDefault = ThreadBudget.Explicit(MaxScanThreads),
        };
        _scheduler = new ScanScheduler(
            NullLogger<ScanScheduler>.Instance, _fs,
            new PinnedSettings(GlobalSettings.Default with { ScanThreading = threading }));

        // Descend everywhere, emit every file: the point is traversal throughput, not policy.
        _options = new ScanSessionOptions
        {
            OnSubdirectory = static (_, tag) => new ChildDecision(true, tag),
            OnFile = static (_, _) => true,
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // Before the tree: a worker still walking would fight the delete, and the scheduler's
        // LongRunning threads otherwise linger 15s past their last work item.
        _scheduler.Dispose();
        if (Source == Backing.Disk)
            DiskTree.Delete(_root);
    }

    [Benchmark]
    public int Walk()
    {
        using IScanSession session = _scheduler.OpenSession(_options, default);
        session.Submit(new ScanWorkItem(_root, VolumeKey, DriveClass.Fixed, null));
        session.CompleteSubmissions();

        int count = 0;
        foreach (ScanResult result in session.Consume())
            if (result.Entry is not null)
                count++;   // fully drain so the whole walk is measured
        return count;
    }

    /// <summary>Settings the scheduler re-reads at every <c>OpenSession</c>, so the pinned budget
    /// takes effect per scan.</summary>
    private sealed class PinnedSettings(GlobalSettings settings) : ISettingsProvider
    {
        public GlobalSettings Current => settings;
        public Result<GlobalSettings, string> Update(GlobalSettings updated) =>
            Result<GlobalSettings, string>.Success(updated);
    }

    /// <summary>A pre-built tree served from dictionaries — no syscalls, no page cache, no device
    /// queue. Entries are built with <see cref="FileSystemEntry.InDirectory"/> because that is what
    /// the real service returns; a fake handing out full-path entries would take a different (and
    /// more expensive) path through every downstream consumer and stop being a control.
    /// Modelled on <c>ScanSchedulerTests.BlockingFileSystem</c>.</summary>
    private sealed class InMemoryFileSystem : IFileSystemService
    {
        private readonly Dictionary<string, FileSystemEntry[]> _tree = new(StringComparer.OrdinalIgnoreCase);

        public InMemoryFileSystem(string root, int directoryCount, int filesPerDirectory)
        {
            // Same two-level shape as DiskTree so the two backings walk the same structure.
            Dictionary<string, List<FileSystemEntry>> children = new(StringComparer.OrdinalIgnoreCase);
            List<FileSystemEntry> ChildrenOf(string directory) =>
                children.TryGetValue(directory, out List<FileSystemEntry>? list)
                    ? list
                    : children[directory] = [];

            ChildrenOf(root);
            for (int d = 0; d < directoryCount; d++)
            {
                string group = Path.Combine(root, $"grp{d / 50:D4}");
                string leaf = Path.Combine(group, $"dir{d:D6}");
                if (!children.ContainsKey(group))
                    ChildrenOf(root).Add(FileSystemEntry.InDirectory(root, $"grp{d / 50:D4}", true, 0, default));
                ChildrenOf(group).Add(FileSystemEntry.InDirectory(group, $"dir{d:D6}", true, 0, default));
                List<FileSystemEntry> files = ChildrenOf(leaf);
                for (int i = 0; i < filesPerDirectory; i++)
                    files.Add(FileSystemEntry.InDirectory(
                        leaf, $"f{d * filesPerDirectory + i:D8}.dat", false, 0, default));
            }

            foreach (KeyValuePair<string, List<FileSystemEntry>> entry in children)
                _tree[entry.Key] = [.. entry.Value];
        }

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateEntries(string path)
        {
            if (!_tree.TryGetValue(path, out FileSystemEntry[]? entries))
                yield break;
            foreach (FileSystemEntry entry in entries)
                yield return entry;
        }

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateRoots() =>
            throw new NotSupportedException();
        public Result<string, string> GetHomeDirectory() => throw new NotSupportedException();
        public Result<string?, string> GetParent(string path) => throw new NotSupportedException();
    }
}

/// <summary>What the scheduler enumerates against.</summary>
public enum Backing
{
    /// <summary>A real temp tree — the end-to-end number, device queue included.</summary>
    Disk,

    /// <summary>A dictionary-backed tree — the scheduler's own coordination cost, with I/O removed.</summary>
    InMemory,
}
