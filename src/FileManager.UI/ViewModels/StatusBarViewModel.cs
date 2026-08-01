using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonLabel))]
    [NotifyPropertyChangedFor(nameof(PauseIconKey))]
    public partial bool IsPaused { get; set; }

    public string PauseButtonLabel => IsPaused ? "Resume the engine" : "Pause the engine";

    public string PauseIconKey => IsPaused ? "IconPlay" : "IconPause";

    /// <summary>Set when a pause toggle failed, so the strip can say so. Cleared by anything that
    /// proves the failure is over — a successful toggle, a successful poll, or a pause-changed event —
    /// because it lives in the always-visible strip and had no other writer to retire it.</summary>
    [ObservableProperty]
    public partial string? PauseError { get; set; }

    /// <summary>The engine's most recent job failure (spec §7). The status snapshot has carried this
    /// since the engine started reporting it; without a surface for it, a job that failed while the
    /// activity panel was collapsed — its default state — showed up nowhere at all.</summary>
    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>Raised the first time a poll reports a given <see cref="EngineStatusSnapshot.StartupWarning"/>,
    /// so the shell can put it in the notice bar. Set by the host; null in tests that do not care.
    /// <para>The poll is the only surface that can carry this. The service publishes the matching
    /// <c>engine-warning</c> event microseconds after it opens the pipe, which is before a UI that
    /// launched it has finished subscribing — so in the flow that matters the event is never seen and
    /// the snapshot is the only place the warning still exists.</para></summary>
    public Action<string>? StartupWarningObserved { get; set; }

    /// <summary>The warning already handed to <see cref="StartupWarningObserved"/>. The snapshot repeats
    /// it on every 2 s poll, so without this the notice bar would be rewritten forever; comparing by
    /// value (rather than latching a bool) still lets a service that restarted with a DIFFERENT problem
    /// announce itself.</summary>
    private string? _reportedStartupWarning;

    /// <summary>Applies a <c>pause-changed</c> event so a change made elsewhere (the CLI, another
    /// client) shows up at once rather than up to one poll interval later.</summary>
    public void ApplyPauseChanged(bool paused)
    {
        IsPaused = paused;
        PauseError = null;   // the engine's pause state just moved, so any stale toggle error is spent
    }

    /// <summary>Toggles the engine's global pause (spec §3.2.4). In-flight jobs always run to
    /// completion; only new work is held.</summary>
    [RelayCommand]
    public async Task TogglePauseAsync()
    {
        bool desired = !IsPaused;
        PauseError = null;
        var result = await gateway.SetPausedAsync(desired);
        if (result.IsCanceled)
            return;
        if (result.TryGetError(out IpcError? error))
        {
            Log.Warning("Toggling pause to {Paused} failed: {Code} {Message}", desired, error.Code, error.Message);
            PauseError = $"Could not {(desired ? "pause" : "resume")}: {error.Message}";
            return;   // state untouched; the next poll reconciles
        }
        // The ack IS the confirmation: the service publishes pause-changed only on an actual
        // transition, so waiting for an echo would hang forever on a redundant toggle.
        IsPaused = desired;
        PauseError = null;
    }

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
        // The poll is the authoritative backstop for pause (it covers both the deliberately-absent
        // echo on a redundant toggle and a dropped pause-changed frame). Skipped while a toggle is in
        // flight so a poll issued BEFORE it cannot briefly revert the button.
        if (!TogglePauseCommand.IsRunning)
        {
            IsPaused = snapshot!.Paused;
            // A round-trip that succeeded retires a previous toggle failure — the strip is always
            // visible, so leaving the message up beside a "Connected" status was simply wrong.
            PauseError = null;
        }
        LastError = snapshot!.LastError;
        StatusText = $"Connected · {snapshot.ActiveProfiles} active profile(s)"
            + (snapshot.Paused ? " · PAUSED" : "")
            + (snapshot.JobsInFlight > 0 ? $" · {snapshot.JobsInFlight} job(s) running" : "");

        // A degraded startup, announced once per distinct message. Deliberately after the status text
        // above: this is a notice about the engine, not part of the strip's own state.
        if (snapshot.StartupWarning is { Length: > 0 } startupWarning
            && !string.Equals(startupWarning, _reportedStartupWarning, StringComparison.Ordinal))
        {
            _reportedStartupWarning = startupWarning;
            StartupWarningObserved?.Invoke(startupWarning);
        }
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
