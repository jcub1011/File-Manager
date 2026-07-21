using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

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

        Assert.False(vm.MaxScanThreadsAuto);
        Assert.Equal(4, vm.MaxScanThreadsValue);
        Assert.True(vm.ShowMaxScanThreadsValue);
    }

    [Fact]
    public async Task Save_sends_an_explicit_scan_thread_count()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker()) { MaxScanThreadsAuto = false, MaxScanThreadsValue = 7 };

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(7, sent.ScanThreading.MaxScanThreads.Value);
    }

    [Fact]
    public async Task Save_sends_auto_when_checked()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker()) { MaxScanThreadsAuto = true, MaxScanThreadsValue = 7 };

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.True(sent.ScanThreading.MaxScanThreads.IsAuto);
    }

    [Fact]
    public async Task Save_rejects_duplicate_drive_type_overrides()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        vm.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = DriveClass.Network, Value = 2 });
        vm.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = DriveClass.Network, Value = 4 });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);            // save aborted, no data silently dropped
        Assert.Contains("Duplicate", vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_rejects_specific_drive_keys_that_collide_after_normalization()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker());
        vm.SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = "C:", Value = 2 });
        vm.SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = " c: ", Value = 4 });

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
        Assert.Equal(ThemeMode.Dark, vm.ThemeMode);

        vm.ThemeMode = ThemeMode.Light;
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

        Assert.Equal(@"D:\profiles", vm.ProfilesDirectory);
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
            RelocateResult = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
        };
        FakeFolderPicker picker = new(@"E:\moved");
        SettingsViewModel vm = new(gateway, picker) { ConfirmMoveProfiles = _ => Task.FromResult(true) };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        (string dir, bool move) = Assert.Single(gateway.RelocateCalls);
        Assert.Equal(@"E:\moved", dir);
        Assert.True(move);
        Assert.Equal(@"E:\moved", vm.ProfilesDirectory);
    }

    [Fact]
    public async Task Change_profiles_directory_refreshes_the_profile_list_on_success()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
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
            RelocateResult = new GlobalSettings { ProfilesDirectory = @"E:\fresh" },
        };
        FakeFolderPicker picker = new(@"E:\fresh");
        SettingsViewModel vm = new(gateway, picker) { ConfirmMoveProfiles = _ => Task.FromResult(false) };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        (_, bool move) = Assert.Single(gateway.RelocateCalls);
        Assert.False(move);
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
        Assert.Equal(ServiceStartupMode.RunOnStartup, vm.StartupMode);

        vm.StartupMode = ServiceStartupMode.StartOnProgramOpen;
        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ServiceStartupMode.StartOnProgramOpen, sent.ServiceStartupMode);
    }
}
