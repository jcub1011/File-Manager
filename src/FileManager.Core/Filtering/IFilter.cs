using FileManager.Core.Files;
using System.IO;

namespace FileManager.Core.Filtering;

/// <param name="NormalizedRelativePath">The '/'-normalized <paramref name="RelativePath"/>, computed
/// once by the caller so pattern filters need not re-normalize per rule. Null when the caller did not
/// pre-normalize, in which case pattern filters normalize on demand.</param>
public readonly record struct FilterInput(
    string FullPath, string RelativePath, int Depth, FileMetadata Metadata,
    string? NormalizedRelativePath = null)
{
    /// <summary>Builds an input the way every caller needs it: depth from the relative path, and the
    /// '/'-normalized form ONLY when a pattern rule would consult it, so the common no-glob profile
    /// allocates no normalized string. The one place this is derived — three call sites (the executor's
    /// screen, the dry-run engine, the profile matcher) had verbatim copies of the two helpers plus this
    /// preamble, and a divergence between them would make the three disagree on what a filter matches.</summary>
    public static FilterInput For(string fullPath, string relativePath, FileMetadata metadata, bool hasPatternRules) =>
        new(fullPath, relativePath, SeparatorCount(relativePath), metadata,
            hasPatternRules ? NormalizeSeparators(relativePath) : null);

    /// <summary>Path depth as the separator count of a relative path — what MaxDepth rules compare.</summary>
    public static int SeparatorCount(string value)
    {
        int count = 0;
        foreach (char c in value)
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                count++;
        return count;
    }

    /// <summary>Glob patterns are authored with '/', so a Windows relative path is normalized once per
    /// file rather than once per pattern rule.</summary>
    public static string NormalizeSeparators(string relativePath) =>
        Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace('\\', '/');
}

public interface IFilter
{
    string Description { get; }                              // e.g. "Include glob *.wav|*.flac"
    bool Excludes(in FilterInput input, out string reason);
}
