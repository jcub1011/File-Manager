using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Filtering.Rules;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FileManager.Core.Filtering;

public sealed class FilterCompiler(ILogger<FilterCompiler> logger, TimeProvider time) : IFilterCompiler
{
    // NonBacktracking bounds evaluation on pathological patterns and is AOT-safe;
    // RegexOptions.Compiled is a no-op-or-worse under AOT and is deliberately absent.
    private const RegexOptions PatternOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>Cap on distinct compiled sets held. The live working set is one entry per
    /// (profile, source) filter pair of the active profiles; the cap only bounds accumulation across a
    /// long session of profile edits, where clearing wholesale costs one recompile per active pair.</summary>
    private const int MaxCachedSets = 256;

    // Compiling is expensive — every glob is translated and every Regex built with NonBacktracking,
    // the costliest construction mode — and the executor compiles ONCE PER FILE (JobExecutor.Screen
    // runs per job), so a 50k-file folder run built 50k identical sets. Caching is safe because a
    // CompiledFilterSet is immutable and time-independent at construction: AgeFilter reads the clock
    // inside Excludes, so a reused set does not freeze its age window.
    //
    // Keyed on the FilterSet instances. FilterSet is a record, but its members are IReadOnlyList<string>,
    // so equality is effectively per-instance — a reloaded profile gets a fresh entry rather than a
    // stale hit, which is the safe direction to err in.
    private readonly ConcurrentDictionary<(FilterSet? Profile, FilterSet? Source), Result<CompiledFilterSet, string>> _cache = new();

    public Result<CompiledFilterSet, string> Compile(FilterSet? profileFilters, FilterSet? sourceFilters)
    {
        (FilterSet? profileFilters, FilterSet? sourceFilters) key = (profileFilters, sourceFilters);
        if (_cache.TryGetValue(key, out Result<CompiledFilterSet, string> cached))
            return cached;

        Result<CompiledFilterSet, string> compiled = CompileUncached(profileFilters, sourceFilters);
        if (_cache.Count >= MaxCachedSets)
            _cache.Clear();
        _cache[key] = compiled;
        return compiled;
    }

    private Result<CompiledFilterSet, string> CompileUncached(FilterSet? profileFilters, FilterSet? sourceFilters)
    {
        FilterSet merged = Merge(profileFilters, sourceFilters);
        List<IFilter> rules = [];

        List<CompiledPattern> includes = [];
        if (!TryCompileGlobs(merged.Include, "glob", includes, out string? error) ||
            !TryCompileRegexes(merged.IncludeRegex, includes, out error))
            return error!;
        if (includes.Count > 0)
            rules.Add(new IncludePatternFilter(includes));

        List<CompiledPattern> excludes = [];
        if (!TryCompileGlobs(merged.ExcludeGlob, "glob", excludes, out error) ||
            !TryCompileRegexes(merged.ExcludeRegex, excludes, out error))
            return error!;
        if (excludes.Count > 0)
            rules.Add(new ExcludePatternFilter(excludes));

        if (merged.MinSizeBytes is not null || merged.MaxSizeBytes is not null)
            rules.Add(new SizeBoundsFilter(merged.MinSizeBytes, merged.MaxSizeBytes));

        if (merged.ModifiedWithin is not null || merged.ModifiedOlderThan is not null || merged.CreatedWithin is not null)
            rules.Add(new AgeFilter(merged.ModifiedWithin, merged.ModifiedOlderThan, merged.CreatedWithin, time));

        // Always present: null Attributes still applies the §4.4 defaults
        // (exclude hidden, system, and reparse points).
        rules.Add(new AttributeFilter(merged.Attributes));

        if (merged.MaxDepth is int maxDepth)
            rules.Add(new DepthFilter(maxDepth));

        logger.LogDebug("Compiled filter set with {RuleCount} rules", rules.Count);
        return new CompiledFilterSet(rules, merged.MaxDepth);
    }

    /// <summary>Field-by-field merge: a non-null per-source field wins over the profile-global
    /// one (spec §4 Phase 2). ContentHashDedupe is validator-rejected when true; merged as OR
    /// only so the reserved flag can never silently vanish.</summary>
    internal static FilterSet Merge(FilterSet? profile, FilterSet? source) => new()
    {
        Include = source?.Include ?? profile?.Include,
        ExcludeGlob = source?.ExcludeGlob ?? profile?.ExcludeGlob,
        IncludeRegex = source?.IncludeRegex ?? profile?.IncludeRegex,
        ExcludeRegex = source?.ExcludeRegex ?? profile?.ExcludeRegex,
        MinSizeBytes = source?.MinSizeBytes ?? profile?.MinSizeBytes,
        MaxSizeBytes = source?.MaxSizeBytes ?? profile?.MaxSizeBytes,
        ModifiedWithin = source?.ModifiedWithin ?? profile?.ModifiedWithin,
        ModifiedOlderThan = source?.ModifiedOlderThan ?? profile?.ModifiedOlderThan,
        CreatedWithin = source?.CreatedWithin ?? profile?.CreatedWithin,
        Attributes = source?.Attributes ?? profile?.Attributes,
        MaxDepth = source?.MaxDepth ?? profile?.MaxDepth,
        ContentHashDedupe = (source?.ContentHashDedupe ?? false) || (profile?.ContentHashDedupe ?? false),
    };

    private static bool TryCompileGlobs(
        IReadOnlyList<string>? globs, string kind, List<CompiledPattern> output, out string? error)
    {
        if (globs is not null)
        {
            foreach (string glob in globs)
            {
                if (!TryCompile(GlobTranslator.Translate(glob), $"{kind} {glob}", output, out error))
                    return false;
            }
        }
        error = null;
        return true;
    }

    private static bool TryCompileRegexes(
        IReadOnlyList<string>? regexes, List<CompiledPattern> output, out string? error)
    {
        if (regexes is not null)
        {
            foreach (string regex in regexes)
            {
                if (!TryCompile(regex, $"regex {regex}", output, out error))
                    return false;
            }
        }
        error = null;
        return true;
    }

    private static bool TryCompile(string pattern, string display, List<CompiledPattern> output, out string? error)
    {
        try
        {
            output.Add(new CompiledPattern(new Regex(pattern, PatternOptions), display));
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"pattern \"{display}\" does not compile: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            // NonBacktracking rejects some constructs (backreferences, lookaround).
            error = $"pattern \"{display}\" uses an unsupported construct: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            error = $"pattern \"{display}\" failed to compile unexpectedly: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
