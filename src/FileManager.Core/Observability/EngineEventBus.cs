using FileManager.Contracts.IPC;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Observability;

/// <summary>In-proc pub/sub for engine events (§4.10). <see cref="Publish"/> is a synchronous
/// fan-out over a snapshot of subscribers; each handler is isolated in try/catch so one throwing
/// subscriber cannot break the fan-out. Subscribers must be O(µs) or queue internally (§8) — the
/// IPC broadcast bridge hands each event to a bounded per-connection channel and returns.</summary>
public sealed class EngineEventBus(ILogger<EngineEventBus> logger) : IEngineEventBus
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public void Publish(EngineEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        Subscription[] snapshot;
        lock (_gate)
            snapshot = [.. _subscriptions];

        foreach (Subscription subscription in snapshot)
        {
            try
            {
                subscription.Handler(evt);
            }
            catch (Exception ex)
            {
                // Last-resort catch-and-log: a bad subscriber must not abort the fan-out to the rest.
                logger.LogError(ex, "An engine-event subscriber threw; continuing with the remaining subscribers");
            }
        }
    }

    public IDisposable Subscribe(Action<EngineEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Subscription subscription = new(this, handler);
        lock (_gate)
            _subscriptions.Add(subscription);
        return subscription;
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(EngineEventBus owner, Action<EngineEvent> handler) : IDisposable
    {
        public Action<EngineEvent> Handler { get; } = handler;

        public void Dispose() => owner.Unsubscribe(this);
    }
}
