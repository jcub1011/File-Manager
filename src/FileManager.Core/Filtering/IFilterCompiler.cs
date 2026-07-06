using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using System;

namespace FileManager.Core.Filtering;

public interface IFilterCompiler
{
    /// <summary>Per-source overrides profile-global field-by-field (spec §4 Phase 2).</summary>
    Result<CompiledFilterSet, string> Compile(FilterSet? profileFilters, FilterSet? sourceFilters);
}

public sealed class CompiledFilterSet
{
    public FilterDecision Evaluate(in FilterInput input) => throw new NotImplementedException();
}

public sealed record FilterDecision(bool Matched, string? DecidingRule);
