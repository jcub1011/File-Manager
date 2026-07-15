using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Load_reflects_the_backend_manual_setting()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings
            {
                DryRunConcurrencyMode = ConcurrencyMode.Manual,
                DryRunManualWorkers = 4,
            },
        };
        SettingsViewModel vm = new(gateway);

        await vm.LoadAsync();

        Assert.Equal(ConcurrencyMode.Manual, vm.Mode);
        Assert.Equal(4, vm.ManualWorkers);
        Assert.True(vm.ShowManualWorkers);
    }

    [Fact]
    public async Task Save_sends_the_worker_count_when_manual()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway) { Mode = ConcurrencyMode.Manual, ManualWorkers = 7 };

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ConcurrencyMode.Manual, sent.DryRunConcurrencyMode);
        Assert.Equal(7, sent.DryRunManualWorkers);
    }

    [Fact]
    public async Task Save_omits_the_worker_count_when_automatic()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway) { Mode = ConcurrencyMode.Automatic, ManualWorkers = 7 };

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ConcurrencyMode.Automatic, sent.DryRunConcurrencyMode);
        Assert.Null(sent.DryRunManualWorkers);
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
