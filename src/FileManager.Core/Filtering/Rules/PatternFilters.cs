using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FileManager.Core.Filtering.Rules;

/// <summary>A compiled pattern plus the user-facing text it came from
/// (e.g. "glob *.wav" or "regex ^draft-").</summary>
internal readonly record struct CompiledPattern(Regex Regex, string Display);

/// <summary>Union of include globs + include regexes; excludes when the set is non-empty and
/// nothing matches (spec §4 Phase 2). Matches against the '/'-normalized relative path.</summary>
internal sealed class IncludePatternFilter(IReadOnlyList<CompiledPattern> patterns) : IFilter
{
    public string Description { get; } = "Include patterns " + DescribePatterns(patterns);

    public bool Excludes(in FilterInput input, out string reason)
    {
        string relative = input.NormalizedRelativePath ?? input.RelativePath.Replace('\\', '/');
        foreach (CompiledPattern pattern in patterns)
        {
            if (pattern.Regex.IsMatch(relative))
            {
                reason = string.Empty;
                return false;
            }
        }
        reason = Description;
        return true;
    }

    internal static string DescribePatterns(IReadOnlyList<CompiledPattern> patterns)
    {
        string[] displays = new string[patterns.Count];
        for (int i = 0; i < patterns.Count; i++)
            displays[i] = patterns[i].Display;
        return string.Join("|", displays);
    }
}

/// <summary>Excludes on any match; the reason names the winning pattern.</summary>
internal sealed class ExcludePatternFilter(IReadOnlyList<CompiledPattern> patterns) : IFilter
{
    public string Description { get; } = "Exclude patterns " + IncludePatternFilter.DescribePatterns(patterns);

    public bool Excludes(in FilterInput input, out string reason)
    {
        string relative = input.NormalizedRelativePath ?? input.RelativePath.Replace('\\', '/');
        foreach (CompiledPattern pattern in patterns)
        {
            if (pattern.Regex.IsMatch(relative))
            {
                reason = "Exclude pattern " + pattern.Display;
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }
}
