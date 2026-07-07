using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

public sealed partial class StatusBarViewModel(IIpcGateway gateway) : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Connecting…";

    /// <summary>One status round-trip; the poll loop and tests both call this.</summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var status = await gateway.GetStatusAsync(ct);
        if (status.TryGetError(out IpcError? error))
        {
            IsConnected = false;
            StatusText = $"Disconnected — {error.Message}";
            return;
        }
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
