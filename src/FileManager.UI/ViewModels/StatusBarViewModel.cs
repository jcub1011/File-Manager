using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

public sealed partial class StatusBarViewModel(IIpcGateway gateway) : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // Tracks the last observed reachability so a failure logs once per outage (on the edge into
    // it), not on every 2-second poll. Starts true so an outage present at startup logs once.
    private bool _serviceReachable = true;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Connecting…";

    /// <summary>One status round-trip; the poll loop and tests both call this.</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var status = await gateway.GetStatusAsync(ct);
        if (status.IsCanceled)
            return;                 // shutting down — leave the last status as-is
        if (status.TryGetError(out IpcError? error))
        {
            // Log only on the reachable→unreachable edge — the poll fires every couple of
            // seconds, so logging every failed poll would flood the log during an outage.
            if (_serviceReachable)
                Log.Warning("Status poll lost connection to the service: {Code} {Message}", error.Code, error.Message);
            _serviceReachable = false;
            IsConnected = false;
            StatusText = $"Disconnected — {error.Message}";
            return;
        }
        _serviceReachable = true;
        status.TryGetValue(out EngineStatusSnapshot? snapshot);
        IsConnected = true;
        StatusText = $"Connected · {snapshot!.ActiveProfiles} active profile(s)"
            + (snapshot.Paused ? " · PAUSED" : "")
            + (snapshot.JobsInFlight > 0 ? $" · {snapshot.JobsInFlight} job(s) running" : "");
    }

    /// <summary>UI-thread poll loop (await + Task.Delay, no timer callbacks — §8 cross-thread
    /// rule: collections and properties mutate on the UI thread only).</summary>
    public async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_serviceReachable)
                    Log.Warning(ex, "Status poll failed");
                _serviceReachable = false;
                IsConnected = false;
                StatusText = $"Disconnected — {ex.Message}";
            }
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
