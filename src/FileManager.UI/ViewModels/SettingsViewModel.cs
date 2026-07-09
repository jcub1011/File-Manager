using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Edits the machine-level <see cref="GlobalSettings"/> (currently the dry-run evaluation
/// concurrency). Loads from and saves to the service over IPC. Global scope offers only
/// Automatic/Manual — Inherit is a per-profile-only mode (there is nothing above the global setting
/// to inherit from).</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;

    public SettingsViewModel(IIpcGateway gateway) => _gateway = gateway;

    public IReadOnlyList<ConcurrencyMode> ConcurrencyModeOptions { get; } =
        [ConcurrencyMode.Automatic, ConcurrencyMode.Manual];

    [ObservableProperty] public partial ConcurrencyMode Mode { get; set; } = ConcurrencyMode.Automatic;
    [ObservableProperty] public partial int ManualWorkers { get; set; } = 1;
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool ShowManualWorkers => Mode == ConcurrencyMode.Manual;

    partial void OnModeChanged(ConcurrencyMode value) => OnPropertyChanged(nameof(ShowManualWorkers));

    /// <summary>Set by the host so a successful save can close the dialog. Kept as a callback so the
    /// VM stays window-agnostic (mirrors the folder-picker seam).</summary>
    public Action? RequestClose { get; set; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await _gateway.GetSettingsAsync();
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not load settings: {error.Message}";
                return;
            }
            result.TryGetValue(out GlobalSettings? settings);
            Mode = settings!.DryRunConcurrencyMode == ConcurrencyMode.Manual
                ? ConcurrencyMode.Manual
                : ConcurrencyMode.Automatic;
            ManualWorkers = Math.Max(1, settings.DryRunManualWorkers ?? 1);
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes an error banner, not an unobserved fault.
            Serilog.Log.Error(ex, "Loading global settings failed unexpectedly");
            ErrorMessage = $"Could not load settings: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            GlobalSettings settings = new()
            {
                DryRunConcurrencyMode = Mode,
                DryRunManualWorkers = Mode == ConcurrencyMode.Manual ? Math.Max(1, ManualWorkers) : null,
            };
            var result = await _gateway.SaveSettingsAsync(settings);
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Save failed: {error.Message}";
                return;
            }
            StatusMessage = "Saved.";
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes an error banner, not an unobserved fault.
            Serilog.Log.Error(ex, "Saving global settings failed unexpectedly");
            ErrorMessage = $"Save failed unexpectedly: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
