using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Profiles;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.Placement;

/// <summary>Measures the worst case of <see cref="ConflictResolver.Probe"/> under
/// <see cref="ConflictResolution.RenameSuffix"/> — the sequential <c>File.Exists</c> loop
/// (bounded at <c>SuffixBound</c> = 10,000) that dry-run walks when a target folder is saturated
/// with same-named files. The folder is filled once in setup so the measured call is pure probing:
/// the probe scans "file (1)…file (N)" before finding the free "file (N+1)" slot, i.e. N+1 syscalls.</summary>
[MemoryDiagnoser]
public class ConflictResolverBenchmarks
{
    private ConflictResolver _resolver = null!;
    private string _dir = null!;
    private string _desiredFinalPath = null!;
    private DateTimeOffset _incomingLastWrite;

    /// <summary>How many "file (i).dat" collisions already occupy the folder; the probe must scan
    /// all of them plus the base name. 10,000 hits the resolver's SuffixBound.</summary>
    [Params(100, 1_000, 10_000)]
    public int SaturationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _resolver = new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance);
        _dir = Path.Combine(Path.GetTempPath(), "fm-bench-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // The base name must exist so Probe enters the RenameSuffix branch rather than short-circuiting.
        _desiredFinalPath = Path.Combine(_dir, "file.dat");
        File.WriteAllText(_desiredFinalPath, "x");
        for (int i = 1; i <= SaturationCount; i++)
            File.WriteAllText(Path.Combine(_dir, $"file ({i}).dat"), "x");

        _incomingLastWrite = DateTimeOffset.UtcNow;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a run over it.
        }
    }

    [Benchmark]
    public ConflictOutcome? ProbeSaturated()
    {
        var result = _resolver.Probe(_desiredFinalPath, ConflictResolution.RenameSuffix, _incomingLastWrite);
        result.TryGetValue(out ConflictOutcome? outcome);
        return outcome;   // free slot is "file (SaturationCount + 1).dat"
    }
}
