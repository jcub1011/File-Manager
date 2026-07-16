using FileManager.Contracts.Settings;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real SettingsWindow and ConfirmWindow so runtime-only XAML failures (the new
/// ServiceStartupMode ComboBox bindings, the ConfirmWindow layout) surface here, not in the app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class SettingsWindowSmokeTests(HeadlessSessionFixture headless)
{
    [Fact]
    public async Task Settings_window_loads_with_the_startup_mode_combo()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                GetSettingsResult = new GlobalSettings
                {
                    ServiceStartupMode = ServiceStartupMode.RunOnStartup,
                    ThemeMode = ThemeMode.Dark,
                },
            };
            SettingsViewModel vm = new(gateway);
            await vm.LoadAsync();

            SettingsWindow window = new() { DataContext = vm };
            window.Show();

            Assert.Equal(ServiceStartupMode.RunOnStartup, vm.StartupMode);
            Assert.Contains(ServiceStartupMode.StartAndStopWithProgram, vm.ServiceStartupModeOptions);
            Assert.Equal(ThemeMode.Dark, vm.ThemeMode);
            Assert.Contains(ThemeMode.System, vm.ThemeModeOptions);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Confirm_window_loads_with_its_message()
    {
        await headless.Session.Dispatch(() =>
        {
            ConfirmWindow window = new("2 job(s) are still running. Close anyway?");
            window.Show();
        }, CancellationToken.None);
    }
}
