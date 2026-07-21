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

        SettingsResponse settings = Assert.IsType<SettingsResponse>(response);
        Assert.Equal(Full(newDir), Full(settings.Settings.ProfilesDirectory));
        Assert.Equal(Full(newDir), Full(_settings.Current.ProfilesDirectory));

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

        Assert.IsType<SettingsResponse>(response);
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

        SettingsResponse settings = Assert.IsType<SettingsResponse>(response);
        Assert.Equal(Full(_oldDir), Full(settings.Settings.ProfilesDirectory));
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
