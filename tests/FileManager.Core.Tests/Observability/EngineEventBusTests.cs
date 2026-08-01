using FileManager.Contracts.IPC;
using FileManager.Core.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Observability;

public sealed class EngineEventBusTests
{
    private static EngineEvent Evt() => new EngineWarningEvent { AtUtc = DateTimeOffset.UnixEpoch, Message = "hi" };

    [Fact]
    public void Publish_fans_out_to_all_subscribers()
    {
        EngineEventBus bus = new(NullLogger<EngineEventBus>.Instance);
        int a = 0, b = 0;
        using IDisposable _ = bus.Subscribe(_ => a++);
        using IDisposable __ = bus.Subscribe(_ => b++);

        bus.Publish(Evt());

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void A_disposed_subscription_stops_receiving()
    {
        EngineEventBus bus = new(NullLogger<EngineEventBus>.Instance);
        int count = 0;
        IDisposable subscription = bus.Subscribe(_ => count++);

        bus.Publish(Evt());
        subscription.Dispose();
        bus.Publish(Evt());

        Assert.Equal(1, count);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_break_the_fan_out()
    {
        EngineEventBus bus = new(NullLogger<EngineEventBus>.Instance);
        int reached = 0;
        using IDisposable _ = bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        using IDisposable __ = bus.Subscribe(_ => reached++);

        bus.Publish(Evt());

        Assert.Equal(1, reached);
    }
}
