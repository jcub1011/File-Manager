using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Filtering;

public sealed class FilterCompilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static FilterCompiler NewCompiler() =>
        new(NullLogger<FilterCompiler>.Instance, new FixedTimeProvider(Now));

    private static FilterInput Input(
        string relativePath, long size = 100, int depth = 0,
        bool hidden = false, bool system = false, bool symlink = false,
        DateTimeOffset? modified = null, DateTimeOffset? created = null) => new(
        @"C:\root\" + relativePath.Replace('/', '\\'),
        relativePath,
        depth,
        new FileMetadata
        {
            Length = size,
            LastWritten = modified ?? Now.AddDays(-1),
            Created = created ?? Now.AddDays(-2),
            IsHidden = hidden,
            IsSystem = system,
            IsSymlink = symlink,
        });

    private static CompiledFilterSet Compile(FilterSet? profile, FilterSet? source = null)
    {
        var compiled = NewCompiler().Compile(profile, source);
        Assert.True(compiled.TryGetValue(out CompiledFilterSet? set));
        return set;
    }

    [Fact]
    public void Include_set_excludes_when_nothing_matches()
    {
        CompiledFilterSet set = Compile(new FilterSet { Include = ["*.wav"] });

        Assert.True(set.Evaluate(Input("song.wav")).Matched);
        FilterDecision excluded = set.Evaluate(Input("notes.txt"));
        Assert.False(excluded.Matched);
        Assert.Contains("Include patterns", excluded.DecidingRule);
    }

    [Fact]
    public void Exclude_names_the_winning_pattern()
    {
        CompiledFilterSet set = Compile(new FilterSet { ExcludeGlob = ["*.tmp", "*.bak"] });

        FilterDecision excluded = set.Evaluate(Input("save.bak"));
        Assert.False(excluded.Matched);
        Assert.Contains("*.bak", excluded.DecidingRule);
    }

    [Fact]
    public void Size_bounds_exclude_outside_the_window()
    {
        CompiledFilterSet set = Compile(new FilterSet { MinSizeBytes = 10, MaxSizeBytes = 1000 });

        Assert.True(set.Evaluate(Input("f", size: 500)).Matched);
        Assert.False(set.Evaluate(Input("f", size: 5)).Matched);
        Assert.False(set.Evaluate(Input("f", size: 5000)).Matched);
    }

    [Fact]
    public void Defaults_exclude_hidden_system_and_symlinks()
    {
        CompiledFilterSet set = Compile(null);

        Assert.True(set.Evaluate(Input("plain.txt")).Matched);
        Assert.False(set.Evaluate(Input("h.txt", hidden: true)).Matched);
        Assert.False(set.Evaluate(Input("s.txt", system: true)).Matched);
        Assert.False(set.Evaluate(Input("l.txt", symlink: true)).Matched);
    }

    [Fact]
    public void Attribute_opt_ins_include_them()
    {
        CompiledFilterSet set = Compile(new FilterSet
        {
            Attributes = new AttributeFilterSettings { IncludeHidden = true, IncludeSystem = true, FollowSymlinks = true },
        });

        Assert.True(set.Evaluate(Input("h.txt", hidden: true, system: true, symlink: true)).Matched);
    }

    [Fact]
    public void Max_depth_excludes_deeper_files()
    {
        CompiledFilterSet set = Compile(new FilterSet { MaxDepth = 1 });

        Assert.True(set.Evaluate(Input("a/b.txt", depth: 1)).Matched);
        Assert.False(set.Evaluate(Input("a/b/c.txt", depth: 2)).Matched);
        Assert.Equal(1, set.MaxDepth);
    }

    [Fact]
    public void Age_windows_use_the_injected_clock()
    {
        CompiledFilterSet set = Compile(new FilterSet { ModifiedWithin = TimeSpan.FromHours(2) });

        Assert.True(set.Evaluate(Input("new.txt", modified: Now.AddHours(-1))).Matched);
        Assert.False(set.Evaluate(Input("old.txt", modified: Now.AddHours(-3))).Matched);
    }

    [Fact]
    public void Per_source_fields_override_profile_global_field_by_field()
    {
        CompiledFilterSet set = Compile(
            profile: new FilterSet { Include = ["*.wav"], MaxDepth = 5 },
            source: new FilterSet { Include = ["*.flac"] });   // overrides Include, inherits MaxDepth

        Assert.True(set.Evaluate(Input("a.flac")).Matched);
        Assert.False(set.Evaluate(Input("a.wav")).Matched);
        Assert.Equal(5, set.MaxDepth);
    }

    [Fact]
    public void Uncompilable_regex_fails_naming_the_pattern()
    {
        var compiled = NewCompiler().Compile(new FilterSet { IncludeRegex = ["[unclosed"] }, null);

        Assert.True(compiled.TryGetError(out string? error));
        Assert.Contains("[unclosed", error);
    }
}
