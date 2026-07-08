using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using System.Collections.Generic;

namespace FileManager.Core.Filtering;

public interface IFilterCompiler
{
    /// <summary>Per-source overrides profile-global field-by-field (spec §4 Phase 2).</summary>
    Result<CompiledFilterSet, string> Compile(FilterSet? profileFilters, FilterSet? sourceFilters);
}

/// <summary>The AND-ed, ordered rule list compiled from a merged FilterSet: the first rule that
/// excludes decides, and its description becomes the "deciding filter" the skip log and dry-run
/// report surface (spec §4 Phase 2, §8).</summary>
public sealed class CompiledFilterSet
{
    private readonly IReadOnlyList<IFilter> _rules;

    internal CompiledFilterSet(IReadOnlyList<IFilter> rules, int? maxDepth)
    {
        _rules = rules;
        MaxDepth = maxDepth;
    }

    /// <summary>The merged MaxDepth, exposed so the scanner can prune directories instead of
    /// enumerating whole subtrees only to filter every file out.</summary>
    public int? MaxDepth { get; }

    public FilterDecision Evaluate(in FilterInput input)
    {
        for (int i = 0; i < _rules.Count; i++)
        {
            var rule = _rules[i];
            if (rule.Excludes(in input, out string reason))
                return new FilterDecision(false,
                    string.IsNullOrWhiteSpace(reason) ? rule.Description : reason);
        }

        return new FilterDecision(true, null);
    }
}

public sealed record FilterDecision(bool Matched, string? DecidingRule);
