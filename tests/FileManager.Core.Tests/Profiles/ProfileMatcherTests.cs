using FileManager.Contracts.Profiles;
using FileManager.Core.Filtering;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Profiles;

public sealed class ProfileMatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-match-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly FilterCompiler _filterCompiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);

    public ProfileMatcherTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(_sourceDir);
    }

    private ProfileMatcher Matcher(params Profile[] profiles) =>
        new(new FakeProfileCatalog(profiles), _filterCompiler, NullLogger<ProfileMatcher>.Instance);

    private Profile Profile(bool active = true, FilterSet? filters = null) =>
        TestProfiles.Valid(_sourceDir, Path.Combine(_root, "target")) with { Active = active, Filters = filters };

    [Fact]
    public void Matches_a_folder_under_an_active_source()
    {
        Profile profile = Profile();
        IReadOnlyList<ProfileMatch> matches = Matcher(profile).FindMatches(_sourceDir);
        Assert.Single(matches);
        Assert.Equal(profile.Id, matches[0].ProfileId);
    }

    [Fact]
    public void Excludes_inactive_profiles()
    {
        Assert.Empty(Matcher(Profile(active: false)).FindMatches(_sourceDir));
    }

    [Fact]
    public void Does_not_match_a_path_outside_every_source()
    {
        Assert.Empty(Matcher(Profile()).FindMatches(Path.Combine(_root, "elsewhere")));
    }

    [Fact]
    public void Applies_the_filter_to_a_file_path()
    {
        FilterSet filters = new() { ExcludeGlob = ["*.tmp"] };
        string kept = Path.Combine(_sourceDir, "keep.txt");
        string dropped = Path.Combine(_sourceDir, "skip.tmp");
        File.WriteAllText(kept, "x");
        File.WriteAllText(dropped, "x");

        ProfileMatcher matcher = Matcher(Profile(filters: filters));
        Assert.Single(matcher.FindMatches(kept));
        Assert.Empty(matcher.FindMatches(dropped));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
