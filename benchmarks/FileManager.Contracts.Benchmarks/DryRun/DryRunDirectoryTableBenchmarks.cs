using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;

namespace FileManager.Contracts.Benchmarks.DryRun;

/// <summary>Stage 1's mechanism, isolated from the converter that uses it.
///
/// <para><c>DryRunDirectoryTableBuilder</c>'s claim is that "the directory is carved out of the
/// absolute path as a span and almost always HITS — so the directory string is materialized only on
/// a miss, i.e. once per distinct directory instead of once per entry. At 500k swept entries that is
/// the difference between ~2 directory strings per entry and ~1 per 200." On top of that sit two
/// reference-keyed memos: a two-slot one for the entry's root, and a one-slot one for the pair
/// overload's directory, which "skips the trim + full Ordinal hash of a ~60-char path for 199 of
/// every 200 entries".</para>
///
/// <para>Three tiers over identical input, so <c>Alloc Ratio</c> reads directly as the claim:</para>
/// <list type="number">
///   <item><see cref="GetOrAddFromString"/> — the pre-Stage-1 shape. <c>Path.GetDirectoryName</c>
///   materializes a throwaway string whose only purpose is to be hashed.</item>
///   <item><see cref="ConvertFromAbsolutePath"/> — the span probe plus the root memo.</item>
///   <item><see cref="ConvertFromPair"/> — the fast path the sweep's carriers take, where the
///   directory arrives as a shared instance and hits the memo.</item>
/// </list>
///
/// <para><see cref="FilesPerDirectory"/> is the axis the claim is stated in. At 200 (the production
/// shape) the table hits far more often than it misses and the tiers separate. At 1 every probe is a
/// miss, every directory is genuinely new, and tiers 2 and 3 should converge on tier 1 — which is
/// the honest boundary of the optimization and belongs in the same table as the win.</para>
///
/// <para><b>Reference identity is part of the fixture.</b> Siblings share one directory string
/// instance and every entry shares one root instance, exactly as <c>FileSystemEntry.InDirectory</c>
/// and the engine's interned roots hand them out. A fixture of equal-but-distinct strings would
/// silently measure the memo-miss path.</para></summary>
[MemoryDiagnoser]
public class DryRunDirectoryTableBenchmarks
{
    private const string Root = @"C:\fm-bench\destination-tree";

    private string[] _paths = null!;
    private string[] _directories = null!;
    private string[] _names = null!;

    [Params(100_000, 500_000)]
    public int Entries { get; set; }

    /// <summary>200 is the sweep's real fan-out; 1 is the directory-per-file worst case where the
    /// span probe has nothing to hit.</summary>
    [Params(1, 200)]
    public int FilesPerDirectory { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _paths = new string[Entries];
        _directories = new string[Entries];
        _names = new string[Entries];
        string directory = Directory(0);
        for (int i = 0; i < Entries; i++)
        {
            if (i % FilesPerDirectory == 0)
                directory = Directory(i);
            _directories[i] = directory;   // one instance per sibling group
            _names[i] = $"file-{i:D8}-with-a-realistic-name.dat";
            _paths[i] = directory + '\\' + _names[i];
        }
    }

    /// <summary>Two allocations per entry that the wire never sees: the directory string carved by
    /// <c>Path.GetDirectoryName</c> and discarded after the dictionary probe, and the root's dictionary
    /// lookup paying a full string hash because there was no memo.</summary>
    [Benchmark(Baseline = true)]
    public int GetOrAddFromString()
    {
        DryRunDirectoryTableBuilder builder = new();
        int last = 0;
        for (int i = 0; i < _paths.Length; i++)
        {
            string directory = Path.GetDirectoryName(_paths[i])
                ?? throw new ArgumentException($"'{_paths[i]}' is not an absolute file path");
            last = builder.GetOrAdd(directory) + builder.GetOrAdd(Root) + Path.GetFileName(_paths[i]).Length;
        }
        return last;
    }

    /// <summary>The span-probe overload: the directory is a slice of the path, so nothing is
    /// materialized on a hit. The file name still is — the wire record genuinely carries it.</summary>
    [Benchmark]
    public int ConvertFromAbsolutePath()
    {
        DryRunDirectoryTableBuilder builder = new();
        int last = 0;
        for (int i = 0; i < _paths.Length; i++)
        {
            (int dirIndex, string fileName, int rootIndex) = builder.Convert(_paths[i], Root);
            last = dirIndex + rootIndex + fileName.Length;
        }
        return last;
    }

    /// <summary>The pair overload — allocation-free on a directory-table hit, because the caller
    /// already holds both halves and the file-name string is passed straight through to the wire.
    /// This is what the destination sweep's carriers feed it.</summary>
    [Benchmark]
    public int ConvertFromPair()
    {
        DryRunDirectoryTableBuilder builder = new();
        int last = 0;
        for (int i = 0; i < _directories.Length; i++)
        {
            (int dirIndex, string fileName, int rootIndex) = builder.Convert(_directories[i], _names[i], Root);
            last = dirIndex + rootIndex + fileName.Length;
        }
        return last;
    }

    /// <summary>Two nesting levels and ~96-character paths — the shape the Stage 1 numbers were
    /// measured at. Path length drives every per-entry cost here, so a short-name fixture would
    /// flatter all three tiers.</summary>
    private string Directory(int index) =>
        $@"{Root}\level-one-padding\dir{index / FilesPerDirectory:D6}";
}
