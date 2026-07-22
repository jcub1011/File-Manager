using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.Filtering;

/// <summary>Isolates the pure glob→regex string translation (<see cref="GlobTranslator"/>,
/// internal — reached via InternalsVisibleTo). Separates the parsing/string-building cost from
/// the <see cref="System.Text.RegularExpressions.Regex"/> construction measured in
/// <see cref="FilterCompileBenchmarks"/>.</summary>
[MemoryDiagnoser]
public class GlobTranslationBenchmarks
{
    private string[] _globs = null!;

    [Params(1, 10, 50)]
    public int PatternCount { get; set; }

    [GlobalSetup]
    public void Setup() => _globs = MakeGlobs(PatternCount);

    [Benchmark]
    public int Translate()
    {
        int total = 0;
        foreach (string glob in _globs)
            total += GlobTranslator.Translate(glob).Length;
        return total;
    }

    internal static string[] MakeGlobs(int count)
    {
        string[] shapes = ["**/*.ext{0}", "reports/**/q{0}-*.csv", "*.type{0}", "a/b/c{0}/**/*.log"];
        string[] globs = new string[count];
        for (int i = 0; i < count; i++)
            globs[i] = string.Format(shapes[i % shapes.Length], i);
        return globs;
    }
}

/// <summary>Measures <see cref="FilterCompiler.Compile"/> end to end: merge → glob translation →
/// Regex construction under the engine's NonBacktracking options. This is paid once per source
/// per run, so its cost matters most when profiles carry many patterns.</summary>
[MemoryDiagnoser]
public class FilterCompileBenchmarks
{
    private readonly FilterCompiler _compiler =
        new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
    private FilterSet _filters = null!;

    [Params(1, 10, 50)]
    public int PatternCount { get; set; }

    [GlobalSetup]
    public void Setup() => _filters = new FilterSet
    {
        Include = GlobTranslationBenchmarks.MakeGlobs(PatternCount),
        ExcludeGlob = ["**/.git/**", "**/*.tmp"],
    };

    [Benchmark]
    public bool Compile()
    {
        Result<CompiledFilterSet, string> result = _compiler.Compile(_filters, sourceFilters: null);
        return result.TryGetValue(out _);
    }
}

/// <summary>Measures per-candidate evaluation throughput: the compiled filter set is built once
/// in setup, then <see cref="CompiledFilterSet.Evaluate"/> runs over a batch of file inputs — the
/// cost paid for every file the scanner surfaces.</summary>
[MemoryDiagnoser]
public class FilterMatchBenchmarks
{
    private CompiledFilterSet _compiled = null!;
    private FilterInput[] _inputs = null!;

    /// <summary>Number of candidate files evaluated per invocation.</summary>
    [Params(1_000, 10_000)]
    public int CandidateCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        FilterCompiler compiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
        FilterSet filters = new()
        {
            Include = ["**/*.dat", "**/*.csv"],
            ExcludeGlob = ["**/.git/**", "**/*.tmp"],
            MinSizeBytes = 1,
        };
        compiler.Compile(filters, sourceFilters: null).TryGetValue(out CompiledFilterSet? compiled);
        _compiled = compiled!;

        FileMetadata meta = new()
        {
            Length = 4096,
            LastWritten = DateTimeOffset.UnixEpoch,
            Created = DateTimeOffset.UnixEpoch,
            IsHidden = false,
            IsSystem = false,
            IsSymlink = false,
        };

        _inputs = new FilterInput[CandidateCount];
        for (int i = 0; i < CandidateCount; i++)
        {
            // Mix of matching (.dat/.csv) and non-matching (.tmp) relative paths.
            string ext = (i % 3) switch { 0 => "dat", 1 => "csv", _ => "tmp" };
            string rel = $"dir{i % 8}/sub{i % 4}/file-{i}.{ext}";
            _inputs[i] = new FilterInput($@"C:\src\{rel}", rel, Depth: 2, meta);
        }
    }

    [Benchmark]
    public int Evaluate()
    {
        int matched = 0;
        foreach (FilterInput input in _inputs)
        {
            if (_compiled.Evaluate(in input).Matched)
                matched++;
        }
        return matched;
    }
}
