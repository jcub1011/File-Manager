namespace FileManager.Contracts.Tests;

/// <summary>Properties of the source tree itself, rather than of anything it compiles to.</summary>
public sealed class SourceTreeHygieneTests
{
    /// <summary>No source file may contain a NUL byte.
    ///
    /// <para><b>This has happened.</b> <c>RunSnapshotView.Filter.Key</c> was committed with four literal
    /// NUL bytes where <c>.Append(' ')</c> was intended — a valid <c>char</c> literal that worked as a
    /// hash separator, compiled clean and passed the entire suite. What it broke was the file's
    /// <em>textness</em>: git treated it as binary, so no diff, no blame and no review could read it, and
    /// an editor refused to open it. Nothing else catches that, because nothing else is looking at the
    /// bytes.</para>
    ///
    /// <para>Reads bytes, not text: a decoder would replace or ignore exactly the thing under test.</para></summary>
    [Fact]
    public void No_source_file_contains_a_NUL_byte()
    {
        string root = RepositoryRoot();
        string[] offenders =
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(IsReviewableSource)
                .Where(path => File.ReadAllBytes(path).Contains((byte)0))
                .Select(path => Path.GetRelativePath(root, path))
                .Order(),
        ];

        Assert.True(offenders.Length == 0,
            "these files contain NUL bytes and are therefore BINARY to git — no diff, no blame, no " +
            $"review: {string.Join(", ", offenders)}");
    }

    private static bool IsReviewableSource(string path)
    {
        // Build output and third-party checkouts are not ours to hold to this.
        if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}third_party{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }
        return Path.GetExtension(path) is ".cs" or ".axaml" or ".csproj" or ".slnx" or ".json" or ".md" or ".props";
    }

    /// <summary>Walks up from the test binary to the directory holding the solution. Deriving it rather
    /// than hard-coding a <c>..\..\..</c> hop keeps this working under any output layout.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "File-Manager.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
