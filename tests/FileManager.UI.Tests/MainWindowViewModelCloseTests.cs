using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>Covers RequestCloseAsync: the stop-on-close behavior is mode-dependent, and the
/// active-jobs warning can veto the close.</summary>
public sealed class MainWindowViewModelCloseTests
{
    private static MainWindowViewModel NewVm(FakeIpcGateway gateway) =>
        new(gateway, new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions());

    [Fact]
    public async Task Non_stop_mode_allows_close_and_leaves_the_service_running()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.StartOnProgramOpen },
        };
        MainWindowViewModel vm = NewVm(gateway);

        Assert.True(await vm.RequestCloseAsync());
        Assert.Equal(0, gateway.ShutdownCalls);
    }

    [Fact]
    public async Task Stop_mode_with_no_jobs_shuts_the_service_down_and_allows_close()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.StartAndStopWithProgram },
            StatusResult = new EngineStatusSnapshot(false, 0, 0, 0, null),
        };
        MainWindowViewModel vm = NewVm(gateway);

        Assert.True(await vm.RequestCloseAsync());
        Assert.Equal(1, gateway.ShutdownCalls);
    }

    [Fact]
    public async Task Stop_mode_with_running_jobs_vetoes_close_when_the_user_cancels()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.StartAndStopWithProgram },
            StatusResult = new EngineStatusSnapshot(false, 0, 2, 0, null),
        };
        MainWindowViewModel vm = NewVm(gateway);
        vm.ConfirmClose = _ => Task.FromResult(false);          // user declines

        Assert.False(await vm.RequestCloseAsync());
        Assert.Equal(0, gateway.ShutdownCalls);                 // service left alone
    }

    [Fact]
    public async Task Stop_mode_with_running_jobs_shuts_down_when_the_user_confirms()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.StartAndStopWithProgram },
            StatusResult = new EngineStatusSnapshot(false, 0, 2, 0, null),
        };
        MainWindowViewModel vm = NewVm(gateway);
        vm.ConfirmClose = _ => Task.FromResult(true);           // user confirms

        Assert.True(await vm.RequestCloseAsync());
        Assert.Equal(1, gateway.ShutdownCalls);
    }
}

internal sealed class FakeLogFolder : ILogFolderService
{
    public void OpenLogFolder() { }
}

internal sealed class FakeDryRunItemActions : IDryRunItemActions
{
    public void CopyText(string? text) { }
    public void OpenFile(string path) { }
    public void RevealInExplorer(string path) { }
    public void OpenFolderInExplorer(string path) { }
}
