using BenchmarkDotNet.Attributes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Benchmarks.ViewModels;

/// <summary>The dry-run search predicate, run against every row on every (debounced) keystroke.
///
/// <para><c>DryRunPaths.PathContains</c> claims to be the "allocation-free equivalent of
/// <c>Path.Join(dirPath, fileName).Contains(term)</c>" — the rows deliberately never materialize a
/// joined path, so joining one per row per keystroke would allocate exactly the strings the
/// directory-table report exists to avoid. The two tiers here are that sentence: same rows, same
/// term, same answer, one of them joining.</para>
///
/// <para><see cref="TermShape"/> is what keeps the comparison honest. <c>PathContains</c> takes a
/// fast exit when the term is found inside either half, so a term that matches early would flatter
/// it. <see cref="TermShape.NoMatch"/> forces both <c>Contains</c> probes to fail AND the
/// hand-rolled tail scan to run to completion; <see cref="TermShape.SpanningJoint"/> is the only
/// case that scan exists for — a term straddling the separator, which neither half contains. Both
/// must be in the table or the benchmark measures only the easy path.</para></summary>
[MemoryDiagnoser]
public class DryRunPathsBenchmarks
{
    private const string CommonRoot = @"C:\dst\level-one\level-two";

    private string[] _directories = null!;
    private string[] _names = null!;
    private string _term = null!;

    [Params(50_000, 500_000)]
    public int RowCount { get; set; }

    [Params(TermShape.NoMatchShort, TermShape.NoMatchLong, TermShape.InFileName, TermShape.SpanningJoint)]
    public TermShape Term { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directories = new string[RowCount];
        _names = new string[RowCount];
        string directory = $@"{CommonRoot}\dir000000";
        for (int i = 0; i < RowCount; i++)
        {
            if (i % 200 == 0)
                directory = $@"{CommonRoot}\dir{i / 200:D6}";   // one instance per sibling group
            _directories[i] = directory;
            _names[i] = $"file-{i:D8}-with-a-realistic-name.dat";
        }

        _term = Term switch
        {
            // Present in neither half and not straddling the joint: both probes and the whole tail
            // scan run, and nothing matches. The two lengths matter — see TermShape.
            TermShape.NoMatchShort => "zqx",
            TermShape.NoMatchLong => "zzz-not-present-anywhere",
            TermShape.InFileName => "realistic-name",
            // "<dir>\file-" straddles the separator the join would have inserted, so it exists in
            // neither the directory nor the name — only in the virtual joined path.
            TermShape.SpanningJoint => @"0\file-",
            _ => throw new ArgumentOutOfRangeException(nameof(Term)),
        };
    }

    /// <summary>The obvious implementation: materialize the path, then search it. One ~90-character
    /// string per row per keystroke.</summary>
    [Benchmark(Baseline = true)]
    public int JoinThenContains()
    {
        int matched = 0;
        for (int i = 0; i < _directories.Length; i++)
            if (Path.Join(_directories[i], _names[i]).Contains(_term, StringComparison.OrdinalIgnoreCase))
                matched++;
        return matched;
    }

    /// <summary>The shipping predicate: two ordinal-ignore-case probes over the halves, then a bounded
    /// scan across the virtual joint. No string is created.</summary>
    [Benchmark]
    public int PathContains()
    {
        int matched = 0;
        for (int i = 0; i < _directories.Length; i++)
            if (DryRunPaths.PathContains(_directories[i], _names[i], _term))
                matched++;
        return matched;
    }
}

/// <summary>Where the search term lives relative to the (directory, file name) split — which decides
/// how much of <c>PathContains</c> actually runs.
///
/// <para>Term LENGTH is a second axis hiding inside "no match". The tail scan walks roughly
/// <c>term.Length</c> start positions comparing up to <c>term.Length</c> characters each, so its cost
/// is quadratic in the term — while the baseline's <c>string.Contains(OrdinalIgnoreCase)</c> is
/// vectorized and barely cares. Both lengths are in the table because a single one would either hide
/// the cost or overstate it.</para></summary>
public enum TermShape
{
    /// <summary>Three characters, matching nothing — a realistic partial keystroke, and the most
    /// common state of a search box being typed into.</summary>
    NoMatchShort,

    /// <summary>Twenty-four characters, matching nothing. The tail scan's worst case, and where its
    /// quadratic term shows up against a vectorized <c>Contains</c>.</summary>
    NoMatchLong,

    /// <summary>In the file name. The second probe hits and the tail scan never runs.</summary>
    InFileName,

    /// <summary>Straddling the separator, so it exists only in the joined path. The one case the
    /// hand-rolled tail scan was written for.</summary>
    SpanningJoint,
}
