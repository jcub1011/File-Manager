using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Owns the engine event subscription's lifetime: connect, pump, and on any end — clean close
/// or fault — back off and reconnect, for the app's lifetime. Mirrors
/// <see cref="ViewModels.StatusBarViewModel.RunPollLoopAsync"/>: a UI-side loop started from the
/// composition root, not a gateway concern (the gateway documents "no background retry loop").
/// <para>Delivery is LOSSY — the service gives each subscriber a bounded, drop-oldest frame channel —
/// so the stream is not authoritative. <see cref="Connected"/> fires on every attempt so consumers
/// re-seed from get-recent-jobs + get-status.</para>
/// <para>UI-thread affinity: <see cref="RunAsync"/> is started from the UI thread and uses NO
/// <c>ConfigureAwait(false)</c>, so `await foreach`'s MoveNextAsync awaiter captures the Avalonia
/// context and every handler — and therefore every view-model mutation — resumes on the UI thread
/// (§8). No Dispatcher marshalling, consistent with every other view model.</para>
/// <para>Known gap: <see cref="Connected"/> fires before the subscribe ack lands, because the ack is
/// inside the iterator and only surfaces on the first MoveNextAsync — which then blocks until the
/// first event, and a healthy idle engine never produces one. A job completing in that sub-second
/// window is missed until the next reconcile. Since delivery is already lossy by design, the answer
/// is the activity panel's Refresh button and reconcile-on-open, not a two-phase subscribe.</para></summary>
public sealed class EngineEventPump(IIpcGateway gateway)
{
    /// <summary>Backoff bounds. Internal purely as test seams (mirroring
    /// <see cref="ViewModels.ProfileListViewModel.SearchDebounce"/>) so tests need not wait real time.</summary>
    internal TimeSpan MinBackoff { get; set; } = TimeSpan.FromSeconds(1);
    internal TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Awaited at the start of every attempt — the reconcile hook.</summary>
    public Func<Task>? Connected { get; set; }

    /// <summary>Invoked per received event, on the UI thread.</summary>
    public Action<EngineEvent>? Event { get; set; }

    /// <summary>Invoked when an attempt ended in a fault (not on a clean close).</summary>
    public Action<IpcError>? Faulted { get; set; }

    // Tracks the last observed stream health so a fault logs once per outage (on the edge into it),
    // not on every reconnect attempt. Starts true so an outage present at startup logs once.
    private bool _streamHealthy = true;

    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            bool delivered = false;
            try
            {
                if (Connected is not null)
                    await Connected();

                await foreach (Result<EngineEvent, IpcError> item in gateway.SubscribeEventsAsync(ct))
                {
                    if (item.TryGetError(out IpcError? error))
                    {
                        if (_streamHealthy)
                            Log.Warning("Engine event stream faulted: {Code} {Message}", error.Code, error.Message);
                        _streamHealthy = false;
                        Faulted?.Invoke(error);
                        break;
                    }
                    item.TryGetValue(out EngineEvent? evt);

                    delivered = true;
                    _streamHealthy = true;
                    try
                    {
                        Event?.Invoke(evt!);
                    }
                    catch (Exception ex)
                    {
                        // One misbehaving consumer must not tear down the subscription for the others.
                        Log.Error(ex, "An engine event handler threw for {EventType}", evt!.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Last-resort catch-all (directive): RunAsync is fire-and-forget, so nothing may escape.
                if (_streamHealthy)
                    Log.Error(ex, "The engine event pump failed unexpectedly");
                _streamHealthy = false;
            }

            // A stream that delivered something was healthy, so the next outage starts from the floor.
            backoff = delivered ? MinBackoff : Min(Double(backoff), MaxBackoff);
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static TimeSpan Double(TimeSpan value) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value * 2;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
