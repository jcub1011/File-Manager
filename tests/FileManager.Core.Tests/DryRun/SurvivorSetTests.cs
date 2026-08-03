using FileManager.Core.DryRun;
using FileManager.Core.Jobs;

namespace FileManager.Core.Tests.DryRun;

/// <summary>The destination sweep's span-based exclusion probes must agree exactly with the
/// <see cref="NormalizedPath"/>-based probing they replaced — a divergence in the "excluded"
/// direction silently OMITS a file from a Mirror deletion preview, the one direction the preview
/// must not fail in (docs/dry-run-service-memory.md §11). These are property-style comparisons over
/// the adversarial path shapes: case flips, prefix boundaries ("C:\ab" vs "C:\a"), volume roots,
/// UNC roots, files directly in a root.</summary>
public sealed class SurvivorSetTests
{
    [Theory]
    // exact member
    [InlineData(@"C:\out\file.txt", @"C:\out\file.txt")]
    // case flip on Windows (comparer must match NormalizedPath's)
    [InlineData(@"C:\out\FILE.TXT", @"C:\out\file.txt")]
    [InlineData(@"c:\OUT\file.txt", @"C:\out\file.txt")]
    // root-level file
    [InlineData(@"C:\file.txt", @"C:\file.txt")]
    // UNC
    [InlineData(@"\\server\share\team\doc.bin", @"\\server\share\team\DOC.BIN")]
    public void Span_probe_agrees_with_NormalizedPath_probing_for_members(string added, string probe)
    {
        SurvivorSet survivors = new();
        HashSet<NormalizedPath> reference = [];
        NormalizedPath normalized = NormalizedPath.Create(added).Match(p => p, e => throw new InvalidOperationException(e.Message));
        survivors.Add(normalized);
        reference.Add(normalized);

        bool viaSpan = survivors.Contains(probe.AsSpan());
        bool viaSet = reference.Contains(NormalizedPath.FromCanonical(probe));

        Assert.Equal(viaSet, viaSpan);
        Assert.True(viaSpan, $"'{probe}' should match member '{added}'");
    }

    [Theory]
    // sibling
    [InlineData(@"C:\out\file.txt", @"C:\out\other.txt")]
    // prefix that is not a path boundary
    [InlineData(@"C:\out\file.txt", @"C:\out\file.txt.bak")]
    [InlineData(@"C:\out\file.txt", @"C:\out\file.tx")]
    // same leaf under a prefix-sibling directory
    [InlineData(@"C:\a\file.txt", @"C:\ab\file.txt")]
    public void Span_probe_agrees_with_NormalizedPath_probing_for_non_members(string added, string probe)
    {
        SurvivorSet survivors = new();
        HashSet<NormalizedPath> reference = [];
        NormalizedPath normalized = NormalizedPath.Create(added).Match(p => p, e => throw new InvalidOperationException(e.Message));
        survivors.Add(normalized);
        reference.Add(normalized);

        bool viaSpan = survivors.Contains(probe.AsSpan());
        bool viaSet = reference.Contains(NormalizedPath.FromCanonical(probe));

        Assert.Equal(viaSet, viaSpan);
        Assert.False(viaSpan, $"'{probe}' should not match member '{added}'");
    }

    [Theory]
    // equal path
    [InlineData(@"C:\src\a", @"C:\src\a")]
    // strictly under
    [InlineData(@"C:\src\a\x.txt", @"C:\src\a")]
    [InlineData(@"C:\src\a\deep\er\x.txt", @"C:\src\a")]
    // under a volume root (ancestor keeps its trailing separator)
    [InlineData(@"C:\x.txt", @"C:\")]
    // prefix boundary: NOT under
    [InlineData(@"C:\src\ab\x.txt", @"C:\src\a")]
    [InlineData(@"C:\src\ab", @"C:\src\a")]
    // sibling
    [InlineData(@"C:\other\x.txt", @"C:\src\a")]
    // case flip on the ancestor
    [InlineData(@"C:\SRC\A\x.txt", @"C:\src\a")]
    // UNC
    [InlineData(@"\\server\share\team\x.txt", @"\\server\share")]
    public void IsEqualToOrUnder_agrees_with_Equals_or_IsUnder(string path, string root)
    {
        NormalizedPath ancestor = NormalizedPath.Create(root).Match(p => p, e => throw new InvalidOperationException(e.Message));
        NormalizedPath asPath = NormalizedPath.FromCanonical(path);

        bool viaSpan = NormalizedPath.IsEqualToOrUnder(path.AsSpan(), ancestor);
        bool viaPath = asPath.Equals(ancestor) || asPath.IsUnder(ancestor);

        Assert.Equal(viaPath, viaSpan);
    }
}
