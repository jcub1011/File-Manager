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

    [Fact]
    public async Task Toggling_pause_sends_the_request_and_flips_the_label()
    {
        FakeIpcGateway gateway = new();
        StatusBarViewModel statusBar = new(gateway);

        await statusBar.TogglePauseAsync();

        Assert.Equal([true], gateway.SetPausedCalls);
        Assert.True(statusBar.IsPaused);
        Assert.Contains("Resume", statusBar.PauseButtonLabel);
        Assert.Equal("IconPlay", statusBar.PauseIconKey);
        Assert.Null(statusBar.PauseError);
    }

    [Fact]
    public async Task A_failed_pause_toggle_leaves_the_state_and_reports_it()
    {
        FakeIpcGateway gateway = new() { SetPausedResult = new IpcError("SET_PAUSED_FAILED", "disk full") };
        StatusBarViewModel statusBar = new(gateway);

        await statusBar.TogglePauseAsync();

        Assert.False(statusBar.IsPaused);
        Assert.Contains("disk full", statusBar.PauseError);
    }

    [Fact]
    public void A_pause_changed_event_updates_immediately_without_a_poll()
    {
        StatusBarViewModel statusBar = new(new FakeIpcGateway());

        statusBar.ApplyPauseChanged(true);

        Assert.True(statusBar.IsPaused);
    }

    [Fact]
    public async Task A_poll_completing_during_a_toggle_does_not_revert_it()
    {
        // The service publishes no pause-changed on a redundant toggle, so the ack is the truth; a
        // poll issued BEFORE the toggle must not stomp it.
        FakeIpcGateway gateway = new()
        {
            StatusResult = new EngineStatusSnapshot(Paused: false, 1, 0, 0, null),
            SetPausedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        StatusBarViewModel statusBar = new(gateway);

        Task toggle = statusBar.TogglePauseCommand.ExecuteAsync(null);
        await statusBar.PollOnceAsync();          // reports Paused = false while the toggle is in flight
        Assert.False(statusBar.IsPaused);

        gateway.SetPausedGate!.SetResult();
        await toggle;

        Assert.True(statusBar.IsPaused);
    }

    [Fact]
    public async Task A_poll_reconciles_pause_when_no_toggle_is_in_flight()
    {
        FakeIpcGateway gateway = new()
        {
            StatusResult = new EngineStatusSnapshot(Paused: true, 1, 0, 0, null),
        };
        StatusBarViewModel statusBar = new(gateway);

        await statusBar.PollOnceAsync();

        Assert.True(statusBar.IsPaused);
    }

    /// <summary>The pause error lives in the always-visible strip, and only another toggle attempt ever
    /// cleared it — so one transient SET_PAUSED_FAILED pinned the message there for the rest of the
    /// session, beside a status text reading "Connected". Anything that proves the failure is over must
    /// retire it.</summary>
    [Fact]
    public async Task A_successful_poll_clears_a_stale_pause_error()
    {
        FakeIpcGateway gateway = new() { SetPausedResult = new IpcError("SET_PAUSED_FAILED", "state file locked") };
        StatusBarViewModel statusBar = new(gateway);
        await statusBar.TogglePauseAsync();
        Assert.NotNull(statusBar.PauseError);

        await statusBar.PollOnceAsync();

        Assert.Null(statusBar.PauseError);
    }

    [Fact]
    public async Task A_pause_changed_event_clears_a_stale_pause_error()
    {
        FakeIpcGateway gateway = new() { SetPausedResult = new IpcError("SET_PAUSED_FAILED", "state file locked") };
        StatusBarViewModel statusBar = new(gateway);
        await statusBar.TogglePauseAsync();
        Assert.NotNull(statusBar.PauseError);

        statusBar.ApplyPauseChanged(true);

        Assert.Null(statusBar.PauseError);
    }

    [Fact]
    public async Task A_successful_toggle_clears_a_stale_pause_error()
    {
        FakeIpcGateway gateway = new() { SetPausedResult = new IpcError("SET_PAUSED_FAILED", "state file locked") };
        StatusBarViewModel statusBar = new(gateway);
        await statusBar.TogglePauseAsync();
        Assert.NotNull(statusBar.PauseError);

        gateway.SetPausedResult = true;
        await statusBar.TogglePauseAsync();

        Assert.Null(statusBar.PauseError);
        Assert.True(statusBar.IsPaused);
    }

    /// <summary>The engine's last job failure has to reach a surface. The activity panel starts
    /// collapsed, so a job that failed while it was closed was reported nowhere — and the next success
    /// clears the engine's own field, so it was gone before the user could open the panel.</summary>
    [Fact]
    public async Task A_poll_surfaces_the_engines_last_error_and_clears_it_on_recovery()
    {
        FakeIpcGateway gateway = new()
        {
            StatusResult = new EngineStatusSnapshot(false, 1, 0, 0, LastError: "target volume is read-only"),
        };
        StatusBarViewModel statusBar = new(gateway);

        await statusBar.PollOnceAsync();
        Assert.Equal("target volume is read-only", statusBar.LastError);

        gateway.StatusResult = new EngineStatusSnapshot(false, 1, 0, 0, null);
        await statusBar.PollOnceAsync();
        Assert.Null(statusBar.LastError);
    }

    /// <summary>The startup warning has to come off the POLL. The service publishes the matching
    /// engine-warning event microseconds after it opens the pipe, which is before a UI that launched it
    /// has finished subscribing — so in the flow that matters the event is never seen, and a snapshot
    /// that carries it is the only way the user hears about an empty profile list.</summary>
    [Fact]
    public async Task A_poll_announces_a_startup_warning_once_and_again_only_if_it_changes()
    {
        FakeIpcGateway gateway = new()
        {
            StatusResult = new EngineStatusSnapshot(false, 0, 0, 0, null)
            {
                StartupWarning = "Profiles could not be loaded from D:\\profiles: access denied.",
            },
        };
        List<string> announced = [];
        StatusBarViewModel statusBar = new(gateway) { StartupWarningObserved = announced.Add };

        await statusBar.PollOnceAsync();
        await statusBar.PollOnceAsync();
        await statusBar.PollOnceAsync();

        // The snapshot repeats it on every 2 s poll; the notice bar must not be rewritten every time.
        Assert.Equal(["Profiles could not be loaded from D:\\profiles: access denied."], announced);

        // A service that restarted with a DIFFERENT problem still gets to say so.
        gateway.StatusResult = new EngineStatusSnapshot(false, 0, 0, 0, null)
        {
            StartupWarning = "The configured profiles directory Z:\\share is not available.",
        };
        await statusBar.PollOnceAsync();
        Assert.Equal(2, announced.Count);
        Assert.Contains("Z:\\share", announced[1]);
    }

    [Fact]
    public async Task A_clean_startup_announces_nothing()
    {
        FakeIpcGateway gateway = new() { StatusResult = new EngineStatusSnapshot(false, 1, 0, 0, null) };
        List<string> announced = [];
        StatusBarViewModel statusBar = new(gateway) { StartupWarningObserved = announced.Add };

        await statusBar.PollOnceAsync();

        Assert.Empty(announced);
    }
}
