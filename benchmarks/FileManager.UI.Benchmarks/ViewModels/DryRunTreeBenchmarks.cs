using BenchmarkDotNet.Attributes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Benchmarks.ViewModels;

/// <summary>The dry-run tree view's forest build — measured at "681 MB retained and a ~4.8 s freeze"
/// before the directory-table report, and "116 MB / ~1.6 s" after
/// (docs/dry-run-memory-optimization.md).
///
/// <para>Both tiers call the shipping <see cref="DryRunTreeNode.BuildForest{T}"/>. What differs is
/// the <em>input shape</em>, which is precisely what the optimization changed. Before the
/// directory-table report, a row carried a flat absolute path and the forest had to split one per
/// row; after it, rows carry (directory, file name) where the directory is a reference into the
/// report's materialized table, so the chain is split and its nodes created <b>once per distinct
/// directory</b> rather than once per row. At 200 files to a directory that is a 200:1 difference in
/// path splitting and node creation.</para>
///
/// <para><b>Scope, stated honestly.</b> This measures the build's allocation and time. It does NOT
/// measure the other half of that claim — deferring leaf-node realization to first expand, which is
/// a <em>retention</em> property. BenchmarkDotNet's <c>Allocated</c> column is per-operation
/// allocation, not retained heap; the retention gate is
/// <c>tests/FileManager.UI.Tests/DryRunViewModelMemoryTests.cs</c>, which asserts the forest stays
/// under 90 MB at the deep-path shape. The two are complementary and neither substitutes for the
/// other.</para>
///
/// <para><see cref="PathShape"/> reproduces the two probes the doc reports separately: the shallow
/// benchmark shape (372 → 238 MB) and the realistic deep-path shape (519 → 219 MB). The deep shape
/// is the one that matters — a shallow tree understates every per-path cost.</para></summary>
[MemoryDiagnoser]
public class DryRunTreeBenchmarks
{
    private const string CommonRoot = @"C:\dst";

    private static readonly IReadOnlyList<TreePillSpec> Specs =
    [
        new("untouched", "untouched", "IconUntouched", "Brush.Info"),
        new("new", "new", "IconAdd", "Brush.Success"),
        new("deleted", "deleted", "IconTrash", "Brush.Danger"),
    ];

    /// <summary>One shared instance rather than the production categorizer's per-row list. Both tiers
    /// would pay that list identically, so hoisting it keeps the measured difference to the thing
    /// under test instead of burying it under categorizer noise.</summary>
    private static readonly IReadOnlyList<(string Kind, int Increment)> OneDeleted = [("deleted", 1)];

    private Row[] _sharedDirectoryRows = null!;
    private FlatRow[] _flatPathRows = null!;

    [Params(50_000, 500_000)]
    public int FileCount { get; set; }

    [Params(PathShape.Shallow, PathShape.DeepRealistic)]
    public PathShape Shape { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sharedDirectoryRows = new Row[FileCount];
        _flatPathRows = new FlatRow[FileCount];
        int filesPerDirectory = Shape == PathShape.Shallow ? FileCount / 64 + 1 : 200;
        string directory = Directory(0, filesPerDirectory);
        for (int i = 0; i < FileCount; i++)
        {
            if (i % filesPerDirectory == 0)
                directory = Directory(i, filesPerDirectory);   // one instance per sibling group
            string name = Shape == PathShape.Shallow
                ? $"file-{i}.dat"
                : $"file-{i:D8}-with-a-realistic-name.dat";
            _sharedDirectoryRows[i] = new Row(directory, name, i);
            _flatPathRows[i] = new FlatRow(directory + '\\' + name, i);
        }
    }

    /// <summary>The pre-directory-table shape: every row holds a flat absolute path, so the forest
    /// derives a directory string per row and splits a chain per row.</summary>
    [Benchmark(Baseline = true)]
    public int BuildForestFromSplitPaths() =>
        DryRunTreeNode.BuildForest(
            _flatPathRows,
            static r => Path.GetDirectoryName(r.FullPath) ?? "",
            static r => Path.GetFileName(r.FullPath),
            static _ => OneDeleted,
            Specs,
            CommonRoot,
            expandedPaths: null,
            static r => r.SizeBytes).Count;

    /// <summary>The shipping shape: rows already hold (directory, file name), and siblings share the
    /// directory string instance the report's materialized table handed them.</summary>
    [Benchmark]
    public int BuildForestFromSharedDirectories() =>
        DryRunTreeNode.BuildForest(
            _sharedDirectoryRows,
            static r => r.DirPath,
            static r => r.FileName,
            static _ => OneDeleted,
            Specs,
            CommonRoot,
            expandedPaths: null,
            static r => r.SizeBytes).Count;

    private string Directory(int index, int filesPerDirectory) => Shape == PathShape.Shallow
        ? $@"{CommonRoot}\dir-{index / filesPerDirectory}"
        : $@"{CommonRoot}\level-one\level-two\level-three\dir{index / filesPerDirectory:D6}";

    private sealed record Row(string DirPath, string FileName, long SizeBytes);

    private sealed record FlatRow(string FullPath, long SizeBytes);
}

/// <summary>The two probe shapes docs/dry-run-memory-optimization.md reports separately.</summary>
public enum PathShape
{
    /// <summary>64 short directories directly under the root — the original benchmark shape, which
    /// understates every per-path cost.</summary>
    Shallow,

    /// <summary>Three nesting levels, 200 files per leaf, ~90-character paths — the "realistic deep
    /// paths" probe, and the one the headline numbers come from.</summary>
    DeepRealistic,
}
