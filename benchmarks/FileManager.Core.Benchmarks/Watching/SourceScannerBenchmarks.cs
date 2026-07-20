using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Scanning;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using FileManager.Contracts.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.Watching;

/// <summary>Measures <see cref="SourceScanner"/> over the real <see cref="FileSystemService"/> —
/// the explicit-stack DFS that turns a profile into candidate payloads and the main throughput
/// path over large source trees. A temp tree is materialized once in setup; the measured method
/// fully drains the lazy result sequence.</summary>
[MemoryDiagnoser]
public class SourceScannerBenchmarks
{
    private SourceScanner _scanner = null!;
    private Profile _profile = null!;
    private string _root = null!;

    /// <summary>Total files in the tree; drives how many entries the DFS must yield and filter.</summary>
    [Params(100, 1_000, 10_000)]
    public int FileCount { get; set; }

    /// <summary>Directory nesting depth; exercises the per-entry relative-path/depth computation.</summary>
    [Params(2, 5)]
    public int Depth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-bench-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BuildTree(_root, FileCount, Depth);

        FileSystemService fs = new(NullLogger<FileSystemService>.Instance);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fs, new BenchSettings());
        _scanner = new SourceScanner(TimeProvider.System, scheduler);

        _profile = BaselineProfile(_root);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a run over it.
        }
    }

    [Benchmark]
    public int Scan()
    {
        int count = 0;
        foreach (Result<Payload, EnumerationFault> _ in _scanner.Scan(_profile, TriggerKind.ManualShell))
            count++;   // fully drain the lazy sequence so the whole DFS is measured
        return count;
    }

    /// <summary>Default settings (auto budgets) for the scan scheduler.</summary>
    private sealed class BenchSettings : ISettingsProvider
    {
        public GlobalSettings Current => GlobalSettings.Default;
        public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
            Result<GlobalSettings, string>.Success(settings);
    }

    /// <summary>Spreads <paramref name="fileCount"/> files across a balanced tree of the given
    /// depth so enumeration touches directories, not just one flat folder.</summary>
    private static void BuildTree(string root, int fileCount, int depth)
    {
        // Fan out enough directories at each level that no single folder dominates.
        int dirsPerLevel = Math.Max(2, (int)Math.Ceiling(Math.Pow(fileCount, 1.0 / (depth + 1))));
        List<string> leaves = [];
        CreateDirs(root, depth, dirsPerLevel, leaves);
        if (leaves.Count == 0)
            leaves.Add(root);

        for (int i = 0; i < fileCount; i++)
        {
            string dir = leaves[i % leaves.Count];
            File.WriteAllText(Path.Combine(dir, $"file-{i}.dat"), "x");
        }
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

    /// <summary>A minimal valid profile scoped to the temp tree (mirrors the test suite's
    /// baseline). No filters, so the scanner yields every file as a candidate.</summary>
    private static Profile BaselineProfile(string sourcePath) => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "Benchmark",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = sourcePath }],
        Targets = [new TargetConfig { Path = Path.Combine(Path.GetTempPath(), "fm-bench-scan-target") }],
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
}
