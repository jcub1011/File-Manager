using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Observability;

/// <summary>The engine's in-proc notification primitive: register handlers, fan a value out to a
/// snapshot of them, and isolate each handler so one that throws cannot abort delivery to the rest.
/// <para>Three services grew a verbatim copy of this — the event bus, the profile catalog, and the
/// pause-state service — each with its own gate, list, nested Subscription class and try/catch fan-out
/// loop. One definition means a fix to the isolation or snapshot semantics lands everywhere at once.</para>
/// <para>Notify snapshots under the gate and invokes OUTSIDE it, so a handler that subscribes or
/// disposes during delivery cannot deadlock — and a handler removed mid-fan-out may still receive the
/// in-flight value, which is the same behavior the hand-rolled copies had.</para></summary>
public sealed class SubscriberList<T>(ILogger logger, string subscriberDescription)
{
    private readonly System.Threading.Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public IDisposable Subscribe(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Subscription subscription = new(this, handler);
        lock (_gate)
            _subscriptions.Add(subscription);
        return subscription;
    }

    public void Notify(T value)
    {
        Subscription[] snapshot;
        lock (_gate)
            snapshot = [.. _subscriptions];

        foreach (Subscription subscription in snapshot)
        {
            try
            {
                subscription.Handler(value);
            }
            catch (Exception ex)
            {
                // Last-resort catch-and-log: a bad subscriber must not abort the fan-out to the rest.
                logger.LogError(ex, "A {Subscriber} threw; continuing with the remaining subscribers", subscriberDescription);
            }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(SubscriberList<T> owner, Action<T> handler) : IDisposable
    {
        public Action<T> Handler { get; } = handler;

        public void Dispose() => owner.Unsubscribe(this);
    }
}
