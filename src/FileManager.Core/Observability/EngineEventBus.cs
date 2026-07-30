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
    private readonly SubscriberList<EngineEvent> _subscribers = new(logger, "engine-event subscriber");

    public void Publish(EngineEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _subscribers.Notify(evt);
    }

    public IDisposable Subscribe(Action<EngineEvent> handler) => _subscribers.Subscribe(handler);
}
