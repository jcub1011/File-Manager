using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using System.Text.Json;

namespace FileManager.UI.Tests;

public sealed class ProfileImportExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-impexp-" + Guid.NewGuid().ToString("N"));

    public ProfileImportExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static Profile Sample(string name, Guid? id = null, bool active = true) => new()
    {
        // Must match ProfileValidator.SupportedSchemaVersion (Core is not referenceable from the UI
        // test project): the fake gateway here never validates, so a stale version would green-light
        // an import the real service rejects. ProfileValidatorTests pins the round-trip.
        SchemaVersion = 2,
        Id = id ?? Guid.NewGuid(),
        Name = name,
        Active = active,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false },
        Sources = [new SourceConfig { Path = @"C:\src" }],
        Targets = [new TargetConfig { Path = @"C:\dst" }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.Overwrite,
            OverwriteHandling = OverwriteHandling.DirectOverwrite,
            VerificationMethod = VerificationMethod.XxHash128,
            OnSuccess = OnSuccessAction.KeepSource,
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresOnly, NotifyOnFailure = false },
    };

    [Fact]
    public async Task Export_writes_one_file_per_selected_profile_into_the_folder()
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
            [
                new ProfileSummary(Guid.NewGuid(), "Alpha", true, "Manual"),
                new ProfileSummary(Guid.NewGuid(), "Beta", false, "Manual"),
            ]),
            GetResult = Sample("Alpha"),
        };
        FakeFolderPicker picker = new(_dir);
        ExportProfilesViewModel vm = new(gateway, picker);
        await vm.LoadAsync();

        vm.SelectAllCommand.Execute(null);       // select all
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));   // atomic write leaves no temp
    }

    [Fact]
    public async Task Export_requires_a_selection()
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
                [new ProfileSummary(Guid.NewGuid(), "Alpha", true, "Manual")]),
        };
        ExportProfilesViewModel vm = new(gateway, new FakeFolderPicker(_dir));
        await vm.LoadAsync();

        await vm.ExportCommand.ExecuteAsync(null);   // nothing checked

        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Single_export_writes_the_right_clicked_profile_to_the_chosen_folder()
    {
        Guid id = Guid.NewGuid();
        FakeIpcGateway gateway = new() { GetResult = Sample("My Profile", id) };
        MainWindowViewModel vm = new(gateway, new FakeFolderPicker(_dir), new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));
        ProfileListItem row = new(id, "My Profile", true, "Manual");

        await vm.ExportProfileCommand.ExecuteAsync(row);

        Assert.True(File.Exists(Path.Combine(_dir, "My Profile.json")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Single_export_does_nothing_when_the_folder_picker_is_cancelled()
    {
        FakeIpcGateway gateway = new() { GetResult = Sample("Alpha") };
        MainWindowViewModel vm = new(gateway, new FakeFolderPicker(result: null), new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));

        await vm.ExportProfileCommand.ExecuteAsync(new ProfileListItem(Guid.NewGuid(), "Alpha", true, "Manual"));

        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }

    [Fact]
    public async Task List_rows_carry_the_export_command_for_the_context_menu()
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
                [new ProfileSummary(Guid.NewGuid(), "Alpha", true, "Manual")]),
        };
        MainWindowViewModel shell = new(gateway, new FakeFolderPicker(_dir), new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));

        await shell.List.RefreshAsync();

        // Every row exposes the shell's single-export command so the right-click menu (list + rail) binds.
        Assert.Same(shell.ExportProfileCommand, shell.List.Profiles[0].ExportCommand);
    }

    [Fact]
    public async Task Export_never_overwrites_an_existing_file_in_the_destination()
    {
        string existing = Path.Combine(_dir, "Alpha.json");
        await File.WriteAllTextAsync(existing, "precious earlier export");

        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
                [new ProfileSummary(Guid.NewGuid(), "Alpha", true, "Manual")]),
            GetResult = Sample("Alpha"),
        };
        ExportProfilesViewModel vm = new(gateway, new FakeFolderPicker(_dir));
        await vm.LoadAsync();
        vm.SelectAllCommand.Execute(null);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("precious earlier export", await File.ReadAllTextAsync(existing));   // untouched
        Assert.True(File.Exists(Path.Combine(_dir, "Alpha (2).json")), "the new export must dedupe against disk");
    }

    [Fact]
    public async Task Single_export_dedupes_against_an_existing_file()
    {
        string existing = Path.Combine(_dir, "My Profile.json");
        await File.WriteAllTextAsync(existing, "precious earlier export");

        Guid id = Guid.NewGuid();
        FakeIpcGateway gateway = new() { GetResult = Sample("My Profile", id) };
        MainWindowViewModel vm = new(gateway, new FakeFolderPicker(_dir), new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));

        await vm.ExportProfileCommand.ExecuteAsync(new ProfileListItem(id, "My Profile", true, "Manual"));

        Assert.Equal("precious earlier export", await File.ReadAllTextAsync(existing));   // untouched
        Assert.True(File.Exists(Path.Combine(_dir, "My Profile (2).json")));
    }

    [Fact]
    public async Task Export_partial_failure_reports_on_the_error_bar_and_keeps_the_dialog_open()
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
                [new ProfileSummary(Guid.NewGuid(), "Alpha", true, "Manual")]),
            GetResult = new IpcError("PROFILE_NOT_FOUND", "gone"),
        };
        bool closed = false;
        ExportProfilesViewModel vm = new(gateway, new FakeFolderPicker(_dir)) { RequestClose = () => closed = true };
        await vm.LoadAsync();
        vm.SelectAllCommand.Execute(null);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.False(closed);                    // a failure must stay visible, not vanish with the dialog
        Assert.NotNull(vm.ErrorMessage);         // danger-styled bar, not the success-styled status bar
        Assert.Null(vm.StatusMessage);
        Assert.Contains("gone", vm.ErrorMessage);
    }

    [Fact]
    public async Task Import_saves_each_file_as_a_new_inactive_copy()
    {
        Guid originalId = Guid.NewGuid();
        string file = Path.Combine(_dir, "alpha.json");
        await using (FileStream stream = File.Create(file))
            JsonSerializer.Serialize(stream, Sample("Alpha", originalId, active: true), FileManagerJsonContext.Default.Profile);

        FakeIpcGateway gateway = new();   // SaveResult defaults to saved
        FakeFolderPicker picker = new() { FilesResult = [file] };
        MainWindowViewModel vm = new(gateway, picker, new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));

        await vm.ImportProfilesCommand.ExecuteAsync(null);

        (Profile saved, bool ack) = Assert.Single(gateway.SaveCalls);
        Assert.Equal("Alpha (imported)", saved.Name);
        Assert.NotEqual(originalId, saved.Id);   // a fresh id — never overwrites
        Assert.False(saved.Active);              // imported inactive
        Assert.False(ack);
    }

    [Fact]
    public async Task Import_preview_shows_the_profiles_contents_and_skip_saves_nothing()
    {
        Profile original = Sample("Alpha") with
        {
            Policies = Sample("Alpha").Policies with { OnSuccess = OnSuccessAction.PermanentDelete },
        };
        string file = Path.Combine(_dir, "alpha.json");
        await using (FileStream stream = File.Create(file))
            JsonSerializer.Serialize(stream, original, FileManagerJsonContext.Default.Profile);

        FakeIpcGateway gateway = new();
        FakeFolderPicker picker = new() { FilesResult = [file] };
        MainWindowViewModel vm = new(gateway, picker, new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));

        ImportPreviewViewModel? shown = null;
        vm.ConfirmImport = preview =>
        {
            shown = preview;
            return Task.FromResult(false);   // the user clicks Skip
        };

        await vm.ImportProfilesCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("Alpha", shown!.ProfileName);
        Assert.Contains(@"C:\src", shown.Sources);
        Assert.Contains(@"C:\dst", shown.Targets);
        Assert.True(shown.IsDestructive);                          // PermanentDelete highlighted
        Assert.Contains("PERMANENTLY DELETED", shown.DispositionText);
        Assert.Empty(gateway.SaveCalls);                           // Skip leaves nothing saved
        Assert.Null(vm.List.ErrorMessage);                         // a deliberate skip is not an error
    }

    [Fact]
    public async Task Import_preview_accept_saves_the_copy()
    {
        string file = Path.Combine(_dir, "alpha.json");
        await using (FileStream stream = File.Create(file))
            JsonSerializer.Serialize(stream, Sample("Alpha"), FileManagerJsonContext.Default.Profile);

        FakeIpcGateway gateway = new();
        FakeFolderPicker picker = new() { FilesResult = [file] };
        MainWindowViewModel vm = new(gateway, picker, new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"))
        {
            ConfirmImport = _ => Task.FromResult(true),
        };

        await vm.ImportProfilesCommand.ExecuteAsync(null);

        (Profile saved, _) = Assert.Single(gateway.SaveCalls);
        Assert.Equal("Alpha (imported)", saved.Name);
    }

    [Fact]
    public async Task Import_reports_a_file_that_is_not_valid_json()
    {
        string file = Path.Combine(_dir, "broken.json");
        await File.WriteAllTextAsync(file, "{ not json");

        FakeIpcGateway gateway = new();
        FakeFolderPicker picker = new() { FilesResult = [file] };
        MainWindowViewModel vm = new(gateway, picker, new FakeLogFolder(), new FakeDryRunItemActions(), clientSettingsPath: Path.Combine(_dir, "client-settings.json"));

        await vm.ImportProfilesCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveCalls);
        Assert.NotNull(vm.List.ErrorMessage);
    }
}
