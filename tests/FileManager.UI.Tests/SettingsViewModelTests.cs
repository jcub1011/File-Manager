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
        SettingsViewModel vm = new(gateway);

        await vm.LoadAsync();

        Assert.False(vm.MaxScanThreadsAuto);
        Assert.Equal(4, vm.MaxScanThreadsValue);
        Assert.True(vm.ShowMaxScanThreadsValue);
    }

    [Fact]
    public async Task Save_sends_an_explicit_scan_thread_count()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway) { MaxScanThreadsAuto = false, MaxScanThreadsValue = 7 };

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(7, sent.ScanThreading.MaxScanThreads.Value);
    }

    [Fact]
    public async Task Save_sends_auto_when_checked()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway) { MaxScanThreadsAuto = true, MaxScanThreadsValue = 7 };

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.True(sent.ScanThreading.MaxScanThreads.IsAuto);
    }

    [Fact]
    public async Task Save_rejects_duplicate_drive_type_overrides()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway);
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
        SettingsViewModel vm = new(gateway);
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
        SettingsViewModel vm = new(gateway) { RequestClose = () => closed = true };

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
        SettingsViewModel vm = new(gateway);

        await vm.LoadAsync();
        Assert.Equal(ThemeMode.Dark, vm.ThemeMode);

        vm.ThemeMode = ThemeMode.Light;
        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ThemeMode.Light, sent.ThemeMode);
    }

    [Fact]
    public async Task Load_and_save_round_trip_the_startup_mode()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.RunOnStartup },
        };
        SettingsViewModel vm = new(gateway);

        await vm.LoadAsync();
        Assert.Equal(ServiceStartupMode.RunOnStartup, vm.StartupMode);

        vm.StartupMode = ServiceStartupMode.StartOnProgramOpen;
        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ServiceStartupMode.StartOnProgramOpen, sent.ServiceStartupMode);
    }
}
