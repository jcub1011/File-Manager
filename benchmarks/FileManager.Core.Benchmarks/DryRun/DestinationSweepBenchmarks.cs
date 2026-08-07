using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Benchmarks.Fixtures;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Platform;
using FileManager.Core.Scanning;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>Batched versus streamed destination sweep — the benchmark
/// docs/dry-run-service-memory.md §12 asked for ("a <c>DestinationProjectorSweepBenchmarks</c> is
/// still worth adding to track allocation rate") and that was never written.
///
/// <para>Both tiers are shipping code, so this is a true A/B rather than a reconstruction.
/// <see cref="SweepBatched"/> is <c>DestinationProjector.Sweep</c>: it collects every classified
/// candidate into one list, allocating a fresh <c>PhysicalFile</c> + <c>VirtualFileOperation</c> +
/// path string per pre-existing file, then sorts. <see cref="SweepStreamed"/> is
/// <c>SweepStreamAsync</c>: rented carriers from a <c>SweepCarrierPool</c>, emitted in byte-budgeted
/// chunks and recycled as the consumer advances, so record allocation is capped at roughly (channel
/// depth + 1) chunks' worth <em>independent of entry count</em>.</para>
///
/// <para>The claims under test: ~75 MB of carrier churn at 500k swept entries
/// (<c>SweepCarrierPool</c>'s type comment) and a batched live set of ~145 MB before the first entry
/// is emitted, plus ~23 MB for the caller's copy (<c>DestinationProjector.SweepStreamAsync</c>'s
/// doc). The <c>Allocated</c> column captures the churn half directly; the live-set half shows up as
/// Gen1/Gen2 pressure, since a batched list of hundreds of thousands of records survives every
/// nursery collection taken during the walk while a streamed chunk never does.</para>
///
/// <para><b>Real disk, deliberately.</b> A fake <c>IFileSystemService</c> that returned full-path
/// entries would bypass the <c>DirectoryHint</c> pair fast path the streamed tier depends on (see
/// <c>DestinationProjector</c>'s candidate construction) and would flatter the batched tier by
/// removing the I/O both share. The cost is a multi-minute <c>[GlobalSetup]</c> per parameter
/// combination — see <see cref="DiskTree"/>.</para>
///
/// <para>Correctness of the pool (borrow == return, bounded retention) is asserted by
/// <c>DestinationProjectorTests</c> and <c>DryRunEngineTests</c>; this class measures rate only.</para></summary>
[MemoryDiagnoser]
public class DestinationSweepBenchmarks
{
    private const int FilesPerDirectory = 200;

    private DestinationProjector _projector = null!;
    private ScanScheduler _scheduler = null!;
    private Profile _profile = null!;
    private string _root = null!;

    /// <summary>Pre-existing files under the target root, every one of them an orphan.</summary>
    [Params(20_000, 100_000)]
    public int DestinationFiles { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-bench-sweep-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(_root, "target");
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);   // empty: nothing survives, so every target file is swept
        DiskTree.Build(target, DestinationFiles, FilesPerDirectory, prefix: 'd');

        FileSystemService fs = new(NullLogger<FileSystemService>.Instance);
        _scheduler = new ScanScheduler(NullLogger<ScanScheduler>.Instance, fs, new BenchSettings());
        _projector = new DestinationProjector(
            NullLogger<DestinationProjector>.Instance, new LocalVolumes(), _scheduler);
        _profile = MirrorProfile(source, target);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // Before the tree: the scheduler's workers are LongRunning threads that linger 15s past their
        // last work item, and one still walking the tree would fight the delete.
        _scheduler.Dispose();
        DiskTree.Delete(_root);
    }

    /// <summary>The batched path: every candidate materialized, then sorted. Holds the whole result
    /// at once by construction — which is what the streamed path exists to avoid.</summary>
    [Benchmark(Baseline = true)]
    public int SweepBatched()
    {
        DestinationSweepResult result = _projector.Sweep(_profile, new SurvivorSet(), truncated: false, default);
        return result.Files.Count;   // consume so the work cannot be elided
    }

    /// <summary>The streamed path, drained exactly as <c>DryRunStreamHandler</c> drains it: consume a
    /// chunk fully, then advance — advancing is what recycles its carriers back to the pool.</summary>
    [Benchmark]
    public async Task<int> SweepStreamed()
    {
        int count = 0;
        await foreach (Result<DryRunChunk, string> item in _projector.SweepStreamAsync(
            _profile, new SurvivorSet(), truncated: false,
            destinationIndexBase: 0, chunkByteBudget: DryRunEngine.WireChunkByteBudget,
            progress: null, ct: default))
        {
            if (item.TryGetValue(out DryRunChunk? chunk))
                count += chunk.DestinationFiles.Count;
        }
        return count;
    }

    /// <summary>Default settings (auto budgets) for the scan scheduler — the sweep's concurrency is
    /// the scheduler's business, and pinning it here would measure a different thing than production.
    /// <c>ScanSchedulerBenchmarks</c> is where the thread budget itself is the variable.</summary>
    private sealed class BenchSettings : ISettingsProvider
    {
        public GlobalSettings Current => GlobalSettings.Default;
        public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
            Result<GlobalSettings, string>.Success(settings);
    }

    /// <summary>The benchmark tree lives in the local temp dir; the projector consults
    /// <see cref="IVolumeInfoProvider.IsNetworkPath"/> and the scheduler needs a real key + class for
    /// its per-volume budget. Mirrors <c>DryRunEngineBenchmarks.LocalVolumes</c>.</summary>
    private sealed class LocalVolumes : IVolumeInfoProvider
    {
        public bool IsNetworkPath(string path) => false;
        public DriveClass GetDriveClass(string path) => DriveClass.Fixed;

        public Result<string, string> GetVolumeKey(string path)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return Result<string, string>.Success(Path.TrimEndingDirectorySeparator(root).ToLowerInvariant());
        }

        public Result<long, string> GetAvailableFreeBytes(string path) => throw new NotSupportedException();
        public Result<VolumeCapacity, string> GetVolumeCapacity(string path) => throw new NotSupportedException();
    }

    /// <summary>Mirror with an empty survivor set classifies EVERY destination file as a Deleted
    /// orphan — the maximum-output sweep, and the shape the memory work targets (the same profile
    /// <c>tools/FileManager.MemoryProbe</c> builds).</summary>
    private static Profile MirrorProfile(string sourcePath, string targetPath) => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "Benchmark",
        Active = true,
        SyncMode = SyncMode.Mirror,
        ScanDestination = true,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = sourcePath }],
        Targets = [new TargetConfig { Path = targetPath }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.Skip,
            OverwriteHandling = OverwriteHandling.StageOverwrites,
            VerificationMethod = VerificationMethod.XxHash128,
            OnSuccess = OnSuccessAction.KeepSource,
            ArchiveFolder = null,
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        Filters = null,
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresAndSkips, NotifyOnFailure = true },
    };
}
