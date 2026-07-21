using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core;
using FileManager.Core.Filtering;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC;

public sealed class RelocateProfilesHandlerTests : IDisposable
{
    private readonly string _root;
    private readonly string _oldDir;
    private readonly SettingsService _settings;
    private readonly ProfileStore _store;
    private readonly ProfileCatalog _catalog;
    private readonly RelocateProfilesHandler _handler;

    public RelocateProfilesHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-relocate-" + Guid.NewGuid().ToString("N"));
        _oldDir = Path.Combine(_root, "old");

        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        _settings = new SettingsService(NullLogger<SettingsService>.Instance, paths);
        _settings.Update(_settings.Current with { ProfilesDirectory = _oldDir });

        ProfileValidator validator = new(
            NullLogger<ProfileValidator>.Instance,
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System));
        _store = new ProfileStore(NullLogger<ProfileStore>.Instance, _settings, validator);
        _catalog = new ProfileCatalog(NullLogger<ProfileCatalog>.Instance, _store);
        _handler = new RelocateProfilesHandler(NullLogger<RelocateProfilesHandler>.Instance, _settings, _catalog);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Move_relocates_files_persists_the_directory_and_reloads_the_catalog()
    {
        Profile profile = TestProfiles.Valid();
        Assert.True(_store.Save(profile, acknowledgeWarnings: false).IsSuccess);
        _catalog.Reload();
        Assert.Single(_catalog.All);

        string newDir = Path.Combine(_root, "new");
        var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = newDir, MoveExisting = true });

        RelocateProfilesResponse relocated = Assert.IsType<RelocateProfilesResponse>(response);
        Assert.Equal(Full(newDir), Full(relocated.Settings.ProfilesDirectory));
        Assert.Equal(Full(newDir), Full(_settings.Current.ProfilesDirectory));
        Assert.Equal(1, relocated.MovedCount);
        Assert.Empty(relocated.SkippedFiles);

        Assert.True(File.Exists(Path.Combine(newDir, profile.Id.ToString("D") + ".json")));
        Assert.Empty(Directory.GetFiles(_oldDir, "*.json"));
        Assert.Single(_catalog.All);        // reloaded from the new directory
        Assert.Equal(profile.Id, _catalog.All[0].Id);
    }

    [Fact]
    public async Task Without_move_leaves_old_files_and_uses_the_new_empty_directory()
    {
        Profile profile = TestProfiles.Valid();
        Assert.True(_store.Save(profile, acknowledgeWarnings: false).IsSuccess);
        _catalog.Reload();

        string newDir = Path.Combine(_root, "new");
        var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = newDir, MoveExisting = false });

        Assert.IsType<RelocateProfilesResponse>(response);
        Assert.Equal(Full(newDir), Full(_settings.Current.ProfilesDirectory));

        Assert.True(File.Exists(Path.Combine(_oldDir, profile.Id.ToString("D") + ".json")));   // original stays
        Assert.False(Directory.Exists(newDir) && Directory.GetFiles(newDir, "*.json").Length > 0);
        Assert.Empty(_catalog.All);          // new directory is empty
    }

    [Fact]
    public async Task A_relative_path_is_rejected()
    {
        var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = "relative\\path" });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("PROFILES_RELOCATE_FAILED", error.Code);
        Assert.Equal(Full(_oldDir), Full(_settings.Current.ProfilesDirectory));   // unchanged
    }

    [Fact]
    public async Task Relocating_to_the_current_directory_is_a_no_op()
    {
        var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = _oldDir, MoveExisting = true });

        RelocateProfilesResponse relocated = Assert.IsType<RelocateProfilesResponse>(response);
        Assert.Equal(Full(_oldDir), Full(relocated.Settings.ProfilesDirectory));
    }

    [Fact]
    public async Task A_destination_collision_is_skipped_and_reported_never_overwritten()
    {
        Profile profile = TestProfiles.Valid();
        Assert.True(_store.Save(profile, acknowledgeWarnings: false).IsSuccess);
        string fileName = profile.Id.ToString("D") + ".json";

        // The destination already holds a (stale) copy under the same name.
        string newDir = Path.Combine(_root, "new");
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, fileName), "{ \"stale\": true }");

        var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = newDir, MoveExisting = true });

        RelocateProfilesResponse relocated = Assert.IsType<RelocateProfilesResponse>(response);
        Assert.Equal(0, relocated.MovedCount);
        string skipped = Assert.Single(relocated.SkippedFiles);   // the collision is surfaced, not silent
        Assert.Equal(fileName, skipped);

        Assert.Equal("{ \"stale\": true }", File.ReadAllText(Path.Combine(newDir, fileName)));   // never overwritten
        Assert.True(File.Exists(Path.Combine(_oldDir, fileName)));                               // original left in place
    }

    [Fact]
    public async Task A_failed_move_rolls_back_and_leaves_the_old_state_intact()
    {
        Profile first = TestProfiles.Valid();
        Profile second = TestProfiles.Valid() with { Id = Guid.NewGuid(), Name = "Second" };
        Assert.True(_store.Save(first, acknowledgeWarnings: false).IsSuccess);
        Assert.True(_store.Save(second, acknowledgeWarnings: false).IsSuccess);
        _catalog.Reload();
        Assert.Equal(2, _catalog.All.Count);

        string newDir = Path.Combine(_root, "new");
        // Hold one profile file exclusively so its copy fails mid-relocation.
        string lockedPath = Path.Combine(_oldDir, second.Id.ToString("D") + ".json");
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = newDir, MoveExisting = true });

            ErrorResponse error = Assert.IsType<ErrorResponse>(response);
            Assert.Equal("PROFILES_RELOCATE_FAILED", error.Code);
        }

        Assert.Equal(Full(_oldDir), Full(_settings.Current.ProfilesDirectory));   // commit never happened
        Assert.Equal(2, Directory.GetFiles(_oldDir, "*.json").Length);            // originals all in place
        Assert.Empty(Directory.GetFiles(newDir, "*.json"));                       // partial copies rolled back
        Assert.Equal(2, _catalog.All.Count);                                      // catalog untouched
    }

    [Fact]
    public async Task The_move_only_sweeps_profile_files_not_other_json()
    {
        Profile profile = TestProfiles.Valid();
        Assert.True(_store.Save(profile, acknowledgeWarnings: false).IsSuccess);
        string strayPath = Path.Combine(_oldDir, "settings.json");
        File.WriteAllText(strayPath, "{}");

        string newDir = Path.Combine(_root, "new");
        var response = await _handler.HandleAsync(new RelocateProfilesRequest { NewDirectory = newDir, MoveExisting = true });

        RelocateProfilesResponse relocated = Assert.IsType<RelocateProfilesResponse>(response);
        Assert.Equal(1, relocated.MovedCount);
        Assert.True(File.Exists(strayPath), "a non-profile json file must never be swept along");
        Assert.False(File.Exists(Path.Combine(newDir, "settings.json")));
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
