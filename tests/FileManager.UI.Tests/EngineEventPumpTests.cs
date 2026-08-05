using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;

namespace FileManager.UI.Tests;

/// <summary>The pump owns the reconnect/reconcile policy, so these pin it without a pipe. Backoff is
/// squeezed to milliseconds via the internal test seams — deliberately NOT to zero, which would turn a
/// failing-attempt loop into a hot loop that starves the test host's thread pool.</summary>
public sealed class EngineEventPumpTests
{
    private static EngineEventPump New(FakeIpcGateway gateway) =>
        new(gateway) { MinBackoff = TimeSpan.FromMilliseconds(5), MaxBackoff = TimeSpan.FromMilliseconds(20) };

    private static Result<EngineEvent, IpcError> Pause(bool paused) =>
        Result<EngineEvent, IpcError>.Success(
            new PauseChangedEvent { AtUtc = DateTimeOffset.UnixEpoch, Paused = paused });

    [Fact]
    public async Task Every_attempt_reconciles_before_pumping()
    {
        // Delivery is lossy, so a reconcile on each (re)connect is the correctness mechanism.
        FakeIpcGateway gateway = new();
        gateway.SubscribeSegments.Enqueue([Pause(true)]);
        gateway.SubscribeSegments.Enqueue([Pause(false)]);
        int reconciles = 0;
        List<EngineEvent> seen = [];
        EngineEventPump pump = New(gateway);
        pump.Connected = () => { reconciles++; return Task.CompletedTask; };
        pump.Event = seen.Add;

        using CancellationTokenSource cts = new();
        Task run = pump.RunAsync(cts.Token);
        await WaitUntilAsync(() => seen.Count == 2);
        await cts.CancelAsync();
        await run;

        // >= rather than ==: the pump legitimately begins another attempt (which parks on the
        // exhausted fake) before the cancellation lands.
        Assert.True(reconciles >= 2, $"expected a reconcile per attempt, got {reconciles}");
        Assert.True(gateway.SubscribeCalls >= 2, $"expected a subscribe per attempt, got {gateway.SubscribeCalls}");
    }

    [Fact]
    public async Task Events_are_delivered_in_order()
    {
        FakeIpcGateway gateway = new();
        gateway.SubscribeSegments.Enqueue([Pause(true), Pause(false), Pause(true)]);
        List<bool> flags = [];
        EngineEventPump pump = New(gateway);
        pump.Event = evt => flags.Add(((PauseChangedEvent)evt).Paused);

        using CancellationTokenSource cts = new();
        Task run = pump.RunAsync(cts.Token);
        await WaitUntilAsync(() => flags.Count == 3);
        await cts.CancelAsync();
        await run;

        Assert.Equal([true, false, true], flags);
    }

    [Fact]
    public async Task A_failure_item_ends_the_attempt_and_is_reported()
    {
        FakeIpcGateway gateway = new();
        gateway.SubscribeSegments.Enqueue([Pause(true), new IpcError("IPC_TRANSPORT", "pipe broke"), Pause(false)]);
        List<EngineEvent> seen = [];
        List<IpcError> faults = [];
        EngineEventPump pump = New(gateway);
        pump.Event = seen.Add;
        pump.Faulted = faults.Add;

        using CancellationTokenSource cts = new();
        Task run = pump.RunAsync(cts.Token);
        await WaitUntilAsync(() => faults.Count == 1);
        await cts.CancelAsync();
        await run;

        Assert.Single(faults);
        Assert.Equal("IPC_TRANSPORT", faults[0].Code);
        Assert.Single(seen);   // the item after the failure is never reached
    }

    [Fact]
    public async Task A_throwing_event_handler_does_not_kill_the_pump()
    {
        FakeIpcGateway gateway = new();
        gateway.SubscribeSegments.Enqueue([Pause(true), Pause(false)]);
        int calls = 0;
        EngineEventPump pump = New(gateway);
        pump.Event = _ =>
        {
            calls++;
            throw new InvalidOperationException("hostile handler");
        };

        using CancellationTokenSource cts = new();
        Task run = pump.RunAsync(cts.Token);
        await WaitUntilAsync(() => calls == 2);
        await cts.CancelAsync();
        await run;

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_throwing_reconcile_does_not_kill_the_pump()
    {
        FakeIpcGateway gateway = new();
        int reconciles = 0;
        EngineEventPump pump = New(gateway);
        pump.Connected = () =>
        {
            reconciles++;
            throw new InvalidOperationException("reconcile blew up");
        };

        using CancellationTokenSource cts = new();
        Task run = pump.RunAsync(cts.Token);
        // It keeps retrying rather than dying on the first bad reconcile.
        await WaitUntilAsync(() => reconciles >= 2);
        Assert.False(run.IsCompleted, "the pump must stay alive across a failing reconcile");
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Cancellation_ends_the_loop_without_throwing()
    {
        FakeIpcGateway gateway = new();      // no segments: the fake parks until cancelled
        EngineEventPump pump = New(gateway);

        using CancellationTokenSource cts = new();
        Task run = pump.RunAsync(cts.Token);
        await cts.CancelAsync();

        await run;                            // must complete, not throw
        Assert.True(run.IsCompletedSuccessfully);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }
}
