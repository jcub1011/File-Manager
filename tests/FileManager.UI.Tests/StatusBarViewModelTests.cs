using FileManager.Contracts.IPC;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class StatusBarViewModelTests
{
    [Fact]
    public async Task Successful_poll_maps_the_snapshot()
    {
        FakeIpcGateway gateway = new()
        {
            StatusResult = new EngineStatusSnapshot(Paused: true, ActiveProfiles: 3, JobsInFlight: 2, QueuedPayloads: 0, LastError: null),
        };
        StatusBarViewModel statusBar = new(gateway);

        await statusBar.PollOnceAsync();

        Assert.True(statusBar.IsConnected);
        Assert.Contains("3 active", statusBar.StatusText);
        Assert.Contains("PAUSED", statusBar.StatusText);
        Assert.Contains("2 job(s)", statusBar.StatusText);
    }

    [Fact]
    public async Task Failed_poll_flips_to_disconnected_and_recovers()
    {
        FakeIpcGateway gateway = new() { StatusResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe") };
        StatusBarViewModel statusBar = new(gateway);

        await statusBar.PollOnceAsync();
        Assert.False(statusBar.IsConnected);
        Assert.Contains("no pipe", statusBar.StatusText);

        gateway.StatusResult = new EngineStatusSnapshot(false, 1, 0, 0, null);
        await statusBar.PollOnceAsync();
        Assert.True(statusBar.IsConnected);
    }
}
