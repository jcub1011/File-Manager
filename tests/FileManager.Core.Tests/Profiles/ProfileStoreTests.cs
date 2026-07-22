using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Filtering;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Profiles;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-store-" + Guid.NewGuid().ToString("N"));
        _store = new ProfileStore(
            NullLogger<ProfileStore>.Instance,
            // The store now resolves its directory from settings; point it at <root>\profiles so the
            // tests' expectations (and the temp-dir cleanup) are unchanged.
            new FakeSettingsProvider(GlobalSettings.Default with { ProfilesDirectory = Path.Combine(_root, "profiles") }),
            new ProfileValidator(
                NullLogger<ProfileValidator>.Instance,
                new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Save_load_round_trips_and_leaves_no_temp_file()
    {
        Profile profile = TestProfiles.Valid();

        var saved = _store.Save(profile, acknowledgeWarnings: false);
        Assert.True(saved.TryGetValue(out var issues));
        Assert.Empty(issues);

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "profiles"), "*.tmp"));

        var loaded = _store.Load(profile.Id);
        Assert.True(loaded.TryGetValue(out Profile? roundTripped));
        // Record equality compares collections by reference; compare content field-by-field.
        Assert.Equal(profile.Id, roundTripped.Id);
        Assert.Equal(profile.Name, roundTripped.Name);
        Assert.Equal(profile.Policies, roundTripped.Policies);
        Assert.Equal(profile.Triggers, roundTripped.Triggers);
        Assert.Equal(profile.Logging, roundTripped.Logging);
        Assert.Equal(profile.Sources, roundTripped.Sources);
        Assert.Equal(profile.Targets, roundTripped.Targets);
    }

    [Fact]
    public void Save_with_error_issues_writes_nothing()
    {
        Profile invalid = TestProfiles.Valid() with { Sources = [] };

        var saved = _store.Save(invalid, acknowledgeWarnings: false);

        Assert.True(saved.TryGetValue(out var issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Error);
        Assert.True(_store.Load(invalid.Id).TryGetError(out _));
    }

    [Fact]
    public void Blocking_warning_blocks_unless_acknowledged()
    {
        Profile risky = TestProfiles.Valid();
        risky = risky with
        {
            Policies = risky.Policies with
            {
                VerificationMethod = VerificationMethod.None,
                OnSuccess = OnSuccessAction.PermanentDelete,
            },
        };

        var refused = _store.Save(risky, acknowledgeWarnings: false);
        Assert.True(refused.TryGetValue(out var refusedIssues));
        Assert.Contains(refusedIssues, i => i.Severity == ValidationSeverity.BlockingWarning);
        Assert.True(_store.Load(risky.Id).TryGetError(out _), "an unacknowledged blocking warning must not persist");

        var accepted = _store.Save(risky, acknowledgeWarnings: true);
        Assert.True(accepted.IsSuccess);
        Assert.True(_store.Load(risky.Id).IsSuccess, "an acknowledged blocking warning saves");
    }

    [Fact]
    public void Plain_warnings_do_not_block()
    {
        // Target nested under own source → PROFILE_TARGET_IN_SOURCE_WARN (Warning only).
        Profile chained = TestProfiles.Valid(
            sourcePath: @"C:\fm-test\src", targetPath: @"C:\fm-test\src\nested");

        var saved = _store.Save(chained, acknowledgeWarnings: false);

        Assert.True(saved.TryGetValue(out var issues));
        Assert.Contains(issues, i => i.Severity == ValidationSeverity.Warning);
        Assert.True(_store.Load(chained.Id).IsSuccess);
    }

    [Fact]
    public void LoadAll_skips_a_corrupt_file_and_returns_the_rest()
    {
        Profile good = TestProfiles.Valid();
        _store.Save(good, acknowledgeWarnings: false);
        File.WriteAllText(Path.Combine(_root, "profiles", "corrupt.json"), "{ not json");

        var all = _store.LoadAll();

        Assert.True(all.TryGetValue(out var profiles));
        Assert.Single(profiles);
        Assert.Equal(good.Id, profiles[0].Id);
    }

    [Fact]
    public void Delete_is_idempotent()
    {
        Profile profile = TestProfiles.Valid();
        _store.Save(profile, acknowledgeWarnings: false);

        Assert.True(_store.Delete(profile.Id).IsSuccess);
        Assert.True(_store.Delete(profile.Id).IsSuccess);
        Assert.True(_store.Load(profile.Id).TryGetError(out _));
    }

    [Fact]
    public void Save_validates_against_other_stored_active_profiles()
    {
        Profile first = TestProfiles.Valid(sourcePath: @"C:\fm-test\shared");
        first = first with { Policies = first.Policies with { OnSuccess = OnSuccessAction.MoveToTrash } };
        Assert.True(_store.Save(first, acknowledgeWarnings: false).IsSuccess);

        Profile second = TestProfiles.Valid(sourcePath: @"C:\fm-test\shared", targetPath: @"C:\fm-test\other");
        var saved = _store.Save(second, acknowledgeWarnings: false);

        Assert.True(saved.TryGetValue(out var issues));
        Assert.Contains(issues, i => i.Code == "PROFILE_OVERLAP_DISPOSAL_WARN");
    }
}
