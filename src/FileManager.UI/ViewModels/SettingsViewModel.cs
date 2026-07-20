using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Edits the machine-level <see cref="GlobalSettings"/> — theme, service startup, and the
/// scan/hash thread budgets (<see cref="ScanThreadingSettings"/>). Loads from and saves to the service
/// over IPC. Scan concurrency is global-only now: profiles no longer override it.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;

    public SettingsViewModel(IIpcGateway gateway) => _gateway = gateway;

    public IReadOnlyList<ServiceStartupMode> ServiceStartupModeOptions { get; } =
        [ServiceStartupMode.RunOnStartup, ServiceStartupMode.StartOnProgramOpen, ServiceStartupMode.StartAndStopWithProgram];

    public IReadOnlyList<ThemeMode> ThemeModeOptions { get; } =
        [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark];

    // Shadow "explicit" defaults shown when a budget's Auto is unchecked, matching the engine's auto
    // formulas so the starting number is the value auto would have chosen.
    private static int ScanAutoDefault => Environment.ProcessorCount * 8;
    private static int HashAutoDefault => Math.Max(1, Environment.ProcessorCount - 1);
    private static int PerDriveAutoDefault => Environment.ProcessorCount * 4;

    [ObservableProperty] public partial ThemeMode ThemeMode { get; set; } = ThemeMode.System;
    [ObservableProperty] public partial ServiceStartupMode StartupMode { get; set; } = ServiceStartupMode.StartAndStopWithProgram;

    [ObservableProperty] public partial bool MaxScanThreadsAuto { get; set; } = true;
    [ObservableProperty] public partial int MaxScanThreadsValue { get; set; } = ScanAutoDefault;
    [ObservableProperty] public partial bool MaxHashThreadsAuto { get; set; } = true;
    [ObservableProperty] public partial int MaxHashThreadsValue { get; set; } = HashAutoDefault;
    [ObservableProperty] public partial bool PerDriveDefaultAuto { get; set; } = true;
    [ObservableProperty] public partial int PerDriveDefaultValue { get; set; } = PerDriveAutoDefault;

    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool ShowMaxScanThreadsValue => !MaxScanThreadsAuto;
    public bool ShowMaxHashThreadsValue => !MaxHashThreadsAuto;
    public bool ShowPerDriveDefaultValue => !PerDriveDefaultAuto;

    partial void OnMaxScanThreadsAutoChanged(bool value) => OnPropertyChanged(nameof(ShowMaxScanThreadsValue));
    partial void OnMaxHashThreadsAutoChanged(bool value) => OnPropertyChanged(nameof(ShowMaxHashThreadsValue));
    partial void OnPerDriveDefaultAutoChanged(bool value) => OnPropertyChanged(nameof(ShowPerDriveDefaultValue));

    public ObservableCollection<DriveTypeOverrideRowViewModel> DriveTypeOverrides { get; } = [];
    public ObservableCollection<SpecificDriveOverrideRowViewModel> SpecificDriveOverrides { get; } = [];

    [RelayCommand] private void AddDriveTypeOverride() => DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Value = PerDriveAutoDefault });
    [RelayCommand] private void RemoveDriveTypeOverride(DriveTypeOverrideRowViewModel row) => DriveTypeOverrides.Remove(row);
    [RelayCommand] private void AddSpecificDriveOverride() => SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { Value = PerDriveAutoDefault });
    [RelayCommand] private void RemoveSpecificDriveOverride(SpecificDriveOverrideRowViewModel row) => SpecificDriveOverrides.Remove(row);

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
            ThemeMode = settings!.ThemeMode;
            StartupMode = settings.ServiceStartupMode;

            ScanThreadingSettings st = settings.ScanThreading;
            (MaxScanThreadsAuto, MaxScanThreadsValue) = FromBudget(st.MaxScanThreads, ScanAutoDefault);
            (MaxHashThreadsAuto, MaxHashThreadsValue) = FromBudget(st.MaxHashThreads, HashAutoDefault);
            (PerDriveDefaultAuto, PerDriveDefaultValue) = FromBudget(st.PerDriveDefault, PerDriveAutoDefault);

            DriveTypeOverrides.Clear();
            foreach (KeyValuePair<DriveClass, ThreadBudget> e in st.DriveTypeOverrides)
            {
                (bool auto, int value) = FromBudget(e.Value, PerDriveAutoDefault);
                DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = e.Key, Auto = auto, Value = value });
            }

            SpecificDriveOverrides.Clear();
            foreach (KeyValuePair<string, ThreadBudget> e in st.SpecificDriveOverrides)
            {
                (bool auto, int value) = FromBudget(e.Value, PerDriveAutoDefault);
                SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = e.Key, Auto = auto, Value = value });
            }
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
            // Reject duplicate keys rather than silently collapsing them (last-wins), which would drop a
            // row the user thinks they saved. TryAdd fails on a repeat, so the first conflict aborts.
            Dictionary<DriveClass, ThreadBudget> byType = [];
            foreach (DriveTypeOverrideRowViewModel row in DriveTypeOverrides)
            {
                if (!byType.TryAdd(row.Class, ToBudget(row.Auto, row.Value)))
                {
                    ErrorMessage = $"Duplicate drive-type override for {row.Class}.";
                    return;
                }
            }

            Dictionary<string, ThreadBudget> specific = [];
            foreach (SpecificDriveOverrideRowViewModel row in SpecificDriveOverrides)
            {
                if (string.IsNullOrWhiteSpace(row.VolumeKey))
                    continue;
                string key = row.VolumeKey.Trim().ToLowerInvariant();
                if (!specific.TryAdd(key, ToBudget(row.Auto, row.Value)))
                {
                    ErrorMessage = $"Duplicate volume key \"{key}\".";
                    return;
                }
            }

            GlobalSettings settings = new()
            {
                ThemeMode = ThemeMode,
                ServiceStartupMode = StartupMode,
                ScanThreading = new ScanThreadingSettings
                {
                    MaxScanThreads = ToBudget(MaxScanThreadsAuto, MaxScanThreadsValue),
                    MaxHashThreads = ToBudget(MaxHashThreadsAuto, MaxHashThreadsValue),
                    PerDriveDefault = ToBudget(PerDriveDefaultAuto, PerDriveDefaultValue),
                    DriveTypeOverrides = byType,
                    SpecificDriveOverrides = specific,
                },
            };
            var result = await _gateway.SaveSettingsAsync(settings);
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Save failed: {error.Message}";
                return;
            }
            StatusMessage = "Saved.";
            ThemeApplier.Apply(ThemeMode);      // apply the selected theme app-wide on save
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

    private static (bool Auto, int Value) FromBudget(ThreadBudget budget, int autoDefault) =>
        budget.Value is int v ? (false, v) : (true, autoDefault);

    private static ThreadBudget ToBudget(bool auto, int value) =>
        auto ? ThreadBudget.Auto : ThreadBudget.Explicit(Math.Max(1, value));
}
