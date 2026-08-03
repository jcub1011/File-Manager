using BenchmarkDotNet.Attributes;
using FileManager.Core.Benchmarks.Fixtures;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
using System.Buffers;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>The destination sweep's per-file "does a source write here?" probe — run once for every
/// pre-existing file under every target root, which at the reported workload is 357,000 times.
///
/// <para>Validates Stage 4's <c>SurvivorSet</c> claim: the span probe means "no per-file path string,
/// no <c>NormalizedPath</c> wrapper, which at a 500k-file sweep is the difference between ~100 MB of
/// probe churn and none". Both tiers ask the identical question of the identical set — the lookup
/// hashes the same characters either way — so the <c>Allocated</c> column isolates exactly one thing:
/// the joined path string the baseline has to materialize just to ask.</para>
///
/// <para><see cref="ExcludedPathProbe"/> adds the second half the sweep actually runs: after the
/// survivor lookup misses, the composed span is tested against every source root
/// (<c>NormalizedPath.IsEqualToOrUnder</c>). Together they are <c>DestinationProjector</c>'s
/// <c>IsExcludedPath</c>, whose claim is that the per-file exclusion check "allocates nothing at
/// all", including the <c>ArrayPool</c> fallback for paths past the 512-char stack buffer.</para>
///
/// <para>Pure in-memory: no disk, no scheduler. The point is the per-probe cost, not enumeration.</para></summary>
[MemoryDiagnoser]
public class SurvivorProbeBenchmarks
{
    /// <summary>Mirrors <c>DestinationProjector.IsExcludedPath</c>'s buffer, so the tiers here pay the
    /// same stack-versus-pool decision production does.</summary>
    private const int StackBufferChars = 512;

    private SurvivorSet _survivors = null!;
    private string[] _directories = null!;
    private string[] _names = null!;
    private List<NormalizedPath> _sourceRoots = null!;

    [Params(50_000, 250_000)]
    public int Probes { get; set; }

    /// <summary>Whether the probed paths are in the survivor set. <see cref="HitRate.AllMiss"/> is the
    /// Mirror shape the memory work targets — every destination file an orphan, so every probe misses
    /// and the sweep goes on to emit the entry.</summary>
    [Params(HitRate.AllMiss, HitRate.AllHit)]
    public HitRate Hits { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // One directory instance per 200 siblings, as FileSystemEntry.InDirectory hands them out.
        _directories = new string[Probes];
        _names = new string[Probes];
        string directory = SweepShapes.Directory(0);
        for (int i = 0; i < Probes; i++)
        {
            if (i % SweepShapes.FilesPerDirectory == 0)
                directory = SweepShapes.Directory(i);
            _directories[i] = directory;
            _names[i] = SweepShapes.FileName(i);
        }

        // Populate to the same COUNT either way, so both settings pay a comparable hash-set load
        // factor and the only difference is whether the lookup finds its key. An empty set would make
        // AllMiss trivially fast and the comparison meaningless.
        _survivors = new SurvivorSet();
        for (int i = 0; i < Probes; i++)
        {
            string path = Hits == HitRate.AllHit
                ? _directories[i] + '\\' + _names[i]
                : SweepShapes.Root + @"\somewhere-else\dir" + (i / SweepShapes.FilesPerDirectory) + @"\other-" + i + ".dat";
            _survivors.Add(NormalizedPath.FromCanonical(path));
        }

        // A source root that contains nothing probed here: the sweep's second check must run to
        // completion on every entry rather than short-circuiting on the first root.
        _sourceRoots = [NormalizedPath.FromCanonical(@"C:\fm-bench\source-tree")];
    }

    /// <summary>The pre-Stage-4 shape: join the (directory, name) pair into a real path string, wrap
    /// it, and ask. One ~96-character string per probe, read by nothing else.</summary>
    [Benchmark(Baseline = true)]
    public int ProbeViaNormalizedPath()
    {
        int found = 0;
        for (int i = 0; i < _directories.Length; i++)
        {
            string joined = Path.Join(_directories[i], _names[i]);
            if (_survivors.Contains(NormalizedPath.FromCanonical(joined)))
                found++;
        }
        return found;
    }

    /// <summary>The shipping probe: compose the pair into a stack buffer and look it up through the
    /// set's <c>ReadOnlySpan&lt;char&gt;</c> alternate lookup. Same set, same comparer, same
    /// characters hashed — no string.</summary>
    [Benchmark]
    public int ProbeViaSpan()
    {
        int found = 0;
        for (int i = 0; i < _directories.Length; i++)
            if (SurvivesProbe(_directories[i], _names[i]))
                found++;
        return found;
    }

    /// <summary>The full per-file exclusion check as the sweep runs it: the survivor lookup plus the
    /// under-any-source test, both over one composed span.</summary>
    [Benchmark]
    public int ExcludedPathProbe()
    {
        int excluded = 0;
        for (int i = 0; i < _directories.Length; i++)
            if (IsExcluded(_directories[i], _names[i]))
                excluded++;
        return excluded;
    }

    /// <summary>One probe per call, exactly as the sweep calls <c>IsExcludedPath</c> per enumerated
    /// file. The method boundary is load-bearing: a <c>stackalloc</c> is released when its frame
    /// unwinds, not at the end of a loop iteration, so composing inline in the loops above would
    /// reserve 512 chars per entry and blow the stack at these counts.</summary>
    private bool SurvivesProbe(string directory, string fileName)
    {
        Span<char> buffer = stackalloc char[StackBufferChars];
        ReadOnlySpan<char> composed = Compose(directory, fileName, buffer, out char[]? rented);
        try
        {
            return _survivors.Contains(composed);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <inheritdoc cref="SurvivesProbe"/>
    private bool IsExcluded(string directory, string fileName)
    {
        Span<char> buffer = stackalloc char[StackBufferChars];
        ReadOnlySpan<char> composed = Compose(directory, fileName, buffer, out char[]? rented);
        try
        {
            return _survivors.Contains(composed) || IsUnderAnySource(composed);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private bool IsUnderAnySource(ReadOnlySpan<char> canonicalPath)
    {
        foreach (NormalizedPath root in _sourceRoots)
            if (NormalizedPath.IsEqualToOrUnder(canonicalPath, root))
                return true;
        return false;
    }

    /// <summary>Joins (directory, name) into <paramref name="buffer"/> with <c>Path.Join</c>'s
    /// separator rule, renting from the shared pool when the pair overflows the stack buffer — the
    /// same composition <c>DestinationProjector.IsExcludedPath</c> performs.</summary>
    private static ReadOnlySpan<char> Compose(
        string directory, string fileName, Span<char> buffer, out char[]? rented)
    {
        rented = null;
        bool needsSeparator = directory.Length > 0 && !Path.EndsInDirectorySeparator(directory);
        int length = directory.Length + (needsSeparator ? 1 : 0) + fileName.Length;
        if (length > buffer.Length)
        {
            rented = ArrayPool<char>.Shared.Rent(length);
            buffer = rented;
        }

        directory.CopyTo(buffer);
        int written = directory.Length;
        if (needsSeparator)
            buffer[written++] = Path.DirectorySeparatorChar;
        fileName.CopyTo(buffer[written..]);
        return buffer[..(written + fileName.Length)];
    }
}

/// <summary>Whether every probed path is present in the survivor set.</summary>
public enum HitRate
{
    /// <summary>Nothing probed survives — the Mirror sweep, where every destination file is an
    /// orphan. The volume case and the one the memory work targets.</summary>
    AllMiss,

    /// <summary>Everything probed survives — a re-preview over a fully mirrored target, where the
    /// sweep emits nothing.</summary>
    AllHit,
}
