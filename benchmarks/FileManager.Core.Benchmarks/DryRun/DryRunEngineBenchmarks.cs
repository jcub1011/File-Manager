using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Placement;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>The scenario dimension: whether the target tree is empty (every file is a fresh write,
/// so no hashing runs) or a byte-identical copy of the source (every file takes the unchanged path,
/// so <see cref="FileHasher"/> hashes both source and target).</summary>
public enum DryRunScenario
{
    /// <summary>Empty target: each file resolves to WouldWrite. Isolates the orchestration cost —
    /// scan, filter eval, per-file JSON size measurement, and the final sort — with zero hashing.</summary>
    AllNew,

    /// <summary>Target pre-populated with identical content: each file hits the size-then-SHA-256
    /// unchanged check, exercising the double-hash branch and the per-file source-hash cache.</summary>
    AllUnchanged,
}

/// <summary>Measures <see cref="DryRunEngine.SimulateAsync"/> end-to-end over the real collaborators
/// (scanner, filter compiler, hasher, conflict resolver). This is the top-level per-file loop: for
/// every scanned file it evaluates filters, stats, optionally hashes source + existing target twice,
/// measures the serialized result, then sorts up to 50k results. A temp source tree (and, for the
/// unchanged scenario, a matching target tree) is materialized once in setup; the measured method
/// runs the whole simulation.</summary>
[MemoryDiagnoser]
public class DryRunEngineBenchmarks
{
    private const int FileSizeBytes = 4 * 1024;   // config-sized; big enough that a SHA-256 is real work

    private DryRunEngine _engine = null!;
    private Guid _profileId;
    private string _sourceRoot = null!;
    private string _targetRoot = null!;

    /// <summary>Files in the source tree; drives how many times the per-file loop body runs
    /// (stays under the engine's <c>MaxReportedFiles</c> = 50k cap so nothing is truncated).</summary>
    [Params(1_000, 10_000)]
    public int FileCount { get; set; }

    [Params(DryRunScenario.AllNew, DryRunScenario.AllUnchanged)]
    public DryRunScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "fm-bench-dryrun-" + Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(baseDir, "source");
        _targetRoot = Path.Combine(baseDir, "target");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_targetRoot);

        BuildTree(_sourceRoot, FileCount, depth: 3);

        // AllUnchanged needs a byte-identical target so every file takes the SHA-256 unchanged path;
        // PreserveStructure + single source means the prospective path mirrors the relative path.
        if (Scenario == DryRunScenario.AllUnchanged)
            CopyTree(_sourceRoot, _targetRoot);

        Profile profile = BaselineProfile(_sourceRoot, _targetRoot);
        _profileId = profile.Id;

        var fileSystem = new FileSystemService(NullLogger<FileSystemService>.Instance);
        var scanner = new SourceScanner(NullLogger<SourceScanner>.Instance, fileSystem, TimeProvider.System);

        _engine = new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new SingleProfileCatalog(profile),
            scanner,
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            new FixedSettings(),
            TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, fileSystem, new LocalVolumes()));
    }

    /// <summary>The benchmark trees live in the local temp dir; the projector only consults
    /// <see cref="IVolumeInfoProvider.IsNetworkPath"/>, so the rest is never reached here.</summary>
    private sealed class LocalVolumes : IVolumeInfoProvider
    {
        public bool IsNetworkPath(string path) => false;
        public Result<long, string> GetAvailableFreeBytes(string path) => throw new NotSupportedException();
        public Result<string, string> GetVolumeKey(string path) => throw new NotSupportedException();
        public Result<VolumeCapacity, string> GetVolumeCapacity(string path) => throw new NotSupportedException();
    }

    /// <summary>Automatic concurrency (the default) so the benchmark measures the engine's own
    /// worker-count choice, not a configured override.</summary>
    private sealed class FixedSettings : ISettingsProvider
    {
        public GlobalSettings Current => GlobalSettings.Default;
        public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
            Result<GlobalSettings, string>.Success(settings);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            // Both roots share a parent temp dir; delete it whole.
            Directory.Delete(Path.GetDirectoryName(_sourceRoot)!, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a run over it.
        }
    }

    [Benchmark]
    public async Task<int> Simulate()
    {
        var result = await _engine.SimulateAsync(_profileId, scopePath: null);
        result.TryGetValue(out var report);
        return report!.SourceFiles.Count;   // consume so the JIT can't elide the work
    }

    /// <summary>Spreads <paramref name="fileCount"/> files with real content across a balanced tree,
    /// mirroring <c>SourceScannerBenchmarks</c>' fixture so enumeration touches directories.</summary>
    private static void BuildTree(string root, int fileCount, int depth)
    {
        byte[] content = new byte[FileSizeBytes];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)(i * 31 + 7);   // deterministic, non-zero

        int dirsPerLevel = Math.Max(2, (int)Math.Ceiling(Math.Pow(fileCount, 1.0 / (depth + 1))));
        List<string> leaves = [];
        CreateDirs(root, depth, dirsPerLevel, leaves);
        if (leaves.Count == 0)
            leaves.Add(root);

        for (int i = 0; i < fileCount; i++)
            File.WriteAllBytes(Path.Combine(leaves[i % leaves.Count], $"file-{i}.dat"), content);
    }

    private static void CreateDirs(string parent, int depth, int dirsPerLevel, List<string> leaves)
    {
        if (depth == 0)
        {
            leaves.Add(parent);
            return;
        }
        for (int i = 0; i < dirsPerLevel; i++)
        {
            string child = Path.Combine(parent, $"dir-{i}");
            Directory.CreateDirectory(child);
            CreateDirs(child, depth - 1, dirsPerLevel, leaves);
        }
    }

    /// <summary>Copies every file under <paramref name="source"/> to the same relative path under
    /// <paramref name="target"/> so the target is a byte-identical mirror.</summary>
    private static void CopyTree(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>A minimal valid profile scoped to the temp trees (mirrors the scanner benchmark's
    /// baseline). No filters, so every file is a candidate; Sha256 verification so the unchanged
    /// scenario exercises the hasher.</summary>
    private static Profile BaselineProfile(string sourcePath, string targetPath) => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "Benchmark",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = sourcePath }],
        Targets = [new TargetConfig { Path = targetPath }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.Skip,
            OverwriteHandling = OverwriteHandling.StageOverwrites,
            VerificationMethod = VerificationMethod.Sha256,
            OnSuccess = OnSuccessAction.KeepSource,
            ArchiveFolder = null,
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        Filters = null,
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresAndSkips, NotifyOnFailure = true },
    };

    /// <summary>The engine only reads <see cref="IProfileCatalog.All"/> to resolve the profile by id;
    /// a one-entry catalog is all the simulation needs.</summary>
    private sealed class SingleProfileCatalog(Profile profile) : IProfileCatalog
    {
        private readonly IReadOnlyList<Profile> _profiles = [profile];
        public IReadOnlyList<Profile> All => _profiles;
        public IReadOnlyList<Profile> Active => _profiles;
        public IDisposable Subscribe(Action changeHandler) => new Unsubscriber();
        public Result Reload() => Result.Success();

        private sealed class Unsubscriber : IDisposable
        {
            public void Dispose() { }
        }
    }
}
