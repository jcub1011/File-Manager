using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;

namespace FileManager.UI.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Load_reflects_an_explicit_backend_scan_thread_pin()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings
            {
                ScanThreading = new ScanThreadingSettings { MaxScanThreads = ThreadBudget.Explicit(4) },
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());

        await vm.LoadAsync();

        Assert.False(vm.MaxScanThreads.Auto);
        Assert.Equal(4, vm.MaxScanThreads.Value);
        Assert.True(vm.MaxScanThreads.ShowValue);
    }

    [Fact]
    public async Task Save_sends_an_explicit_scan_thread_count()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        vm.MaxScanThreads.Auto = false;
        vm.MaxScanThreads.Value = 7;

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(7, sent.ScanThreading.MaxScanThreads.Value);
    }

    [Fact]
    public async Task Save_sends_auto_when_checked()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        vm.MaxScanThreads.Auto = true;
        vm.MaxScanThreads.Value = 7;

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.True(sent.ScanThreading.MaxScanThreads.IsAuto);
    }

    [Fact]
    public async Task Save_rejects_duplicate_drive_type_overrides()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        vm.DriveOverrides.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = DriveClass.Network, Value = 2 });
        vm.DriveOverrides.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = DriveClass.Network, Value = 4 });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);            // save aborted, no data silently dropped
        Assert.Contains("Duplicate", vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_rejects_specific_drive_keys_that_collide_after_normalization()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        vm.DriveOverrides.SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = "C:", Value = 2 });
        vm.DriveOverrides.SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = " c: ", Value = 4 });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);
        Assert.Contains("Duplicate", vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_requests_close_on_success()
    {
        FakeIpcGateway gateway = new();
        bool closed = false;
        SettingsViewModel vm = new(gateway, new FakeFolderPicker()) { RequestClose = () => closed = true };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
    }

    [Fact]
    public async Task Load_and_save_round_trip_the_theme_mode()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ThemeMode = ThemeMode.Dark },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());

        await vm.LoadAsync();
        Assert.Equal(ThemeMode.Dark, vm.Theme.Value);

        vm.Theme.Value = ThemeMode.Light;
        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ThemeMode.Light, sent.ThemeMode);
    }

    [Fact]
    public async Task Load_reflects_the_profiles_directory()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ProfilesDirectory = @"D:\profiles" },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());

        await vm.LoadAsync();

        Assert.Equal(@"D:\profiles", vm.ProfilesDirectory.Value);
    }

    [Fact]
    public async Task Save_carries_the_profiles_directory_through_unchanged()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ProfilesDirectory = @"D:\profiles" },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        await vm.LoadAsync();

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(@"D:\profiles", sent.ProfilesDirectory);
    }

    [Fact]
    public async Task Change_profiles_directory_relocates_with_the_move_choice_and_updates_the_path()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
                MovedCount = 1,
            },
        };
        FakeFolderPicker picker = new(@"E:\moved");
        SettingsViewModel vm = new(gateway, picker) { ConfirmMoveProfiles = _ => Task.FromResult(true) };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        (string dir, bool move) = Assert.Single(gateway.RelocateCalls);
        Assert.Equal(@"E:\moved", dir);
        Assert.True(move);
        Assert.Equal(@"E:\moved", vm.ProfilesDirectory.Value);
    }

    [Fact]
    public async Task Change_profiles_directory_refreshes_the_profile_list_on_success()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
            },
        };
        bool refreshed = false;
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\moved"))
        {
            ConfirmMoveProfiles = _ => Task.FromResult(false),
            ProfilesRelocated = () => { refreshed = true; return Task.CompletedTask; },
        };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.True(refreshed);
    }

    [Fact]
    public async Task Change_profiles_directory_defaults_to_not_moving_when_declined()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\fresh" },
            },
        };
        FakeFolderPicker picker = new(@"E:\fresh");
        SettingsViewModel vm = new(gateway, picker) { ConfirmMoveProfiles = _ => Task.FromResult(false) };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        (_, bool move) = Assert.Single(gateway.RelocateCalls);
        Assert.False(move);
    }

    [Fact]
    public async Task Change_profiles_directory_surfaces_skipped_collisions_instead_of_clean_success()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
                MovedCount = 2,
                SkippedFiles = ["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.json"],
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\moved"))
        {
            ConfirmMoveProfiles = _ => Task.FromResult(true),
        };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\moved", vm.ProfilesDirectory.Value);   // the switch itself succeeded
        Assert.Null(vm.StatusMessage);                           // ...but it is not reported as a clean success
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("stayed in the old folder", vm.ErrorMessage);
    }

    [Fact]
    public async Task Change_profiles_directory_survives_a_corrupt_empty_current_path()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\recovered" },
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\recovered"));
        vm.ProfilesDirectory.Value = "";

        // Corrupt settings hand the VM an empty current path; the command must relocate, not throw
        // out of the command boundary (Path.GetFullPath("") is an ArgumentException).
        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Single(gateway.RelocateCalls);
        Assert.Equal(@"E:\recovered", vm.ProfilesDirectory.Value);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Both_folder_pickers_open_at_the_currently_configured_folder()
    {
        // Re-choosing a folder should start where the setting already points, not at the OS default.
        FakeFolderPicker picker = new(result: null);
        SettingsViewModel vm = new(new FakeIpcGateway(), picker);
        vm.ScratchDirectory.Value = @"D:\scratch";
        vm.ProfilesDirectory.Value = @"E:\profiles";

        await vm.BrowseScratchDirectoryCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\scratch", picker.LastStartNear);

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);
        Assert.Equal(@"E:\profiles", picker.LastStartNear);
    }

    [Fact]
    public async Task Change_profiles_directory_does_nothing_when_the_picker_is_cancelled()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(result: null));

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Empty(gateway.RelocateCalls);
    }

    [Fact]
    public async Task Load_and_save_round_trip_the_startup_mode()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.RunOnStartup },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());

        await vm.LoadAsync();
        Assert.Equal(ServiceStartupMode.RunOnStartup, vm.StartupMode.Value);

        vm.StartupMode.Value = ServiceStartupMode.StartOnProgramOpen;
        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ServiceStartupMode.StartOnProgramOpen, sent.ServiceStartupMode);
    }

    [Fact]
    public async Task Save_refuses_after_a_failed_load_rather_than_persisting_the_defaults()
    {
        // The dialog stays populated after a failed load (the constructor's defaults), so an
        // unguarded Save would rewrite settings.json with them — resetting a relocated
        // ProfilesDirectory and deleting every per-drive override.
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe"),
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());

        await vm.LoadAsync();
        Assert.NotNull(vm.ErrorMessage);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_is_allowed_again_once_a_load_succeeds()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe"),
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        await vm.LoadAsync();

        gateway.GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.RunOnStartup };
        await vm.LoadAsync();
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Single(gateway.SaveSettingsCalls);
    }
}
