using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Shell state: owns the child viewmodels and wires selection → editor/dry-run,
/// save → list refresh, and the unsaved-changes navigation guard.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;
    private readonly ILogFolderService _logFolder;

    public MainWindowViewModel(IIpcGateway gateway, IFolderPicker folderPicker, ILogFolderService logFolder)
    {
        _gateway = gateway;
        _logFolder = logFolder;
        List = new ProfileListViewModel(gateway);
        Editor = new ProfileEditorViewModel(gateway, folderPicker);
        DryRun = new DryRunViewModel(gateway, folderPicker);
        StatusBar = new StatusBarViewModel(gateway);

        List.CanNavigate = () => !Editor.IsDirty;
        List.NavigationBlocked = () => Editor.ShowUnsavedWarning = true;
        List.SelectionCommitted = item => _ = LoadSelectionSafeAsync(item);
        Editor.Saved = profileId => _ = AfterSaveAsync(profileId);
    }

    public ProfileListViewModel List { get; }
    public ProfileEditorViewModel Editor { get; }
    public DryRunViewModel DryRun { get; }
    public StatusBarViewModel StatusBar { get; }

    public async Task InitializeAsync()
    {
        await List.RefreshAsync();
    }

    [RelayCommand]
    public void NewProfile()
    {
        if (Editor.IsDirty)
        {
            Editor.ShowUnsavedWarning = true;
            return;
        }
        List.SelectedProfile = null;
        Editor.LoadNew();
        DryRun.SetProfile(null, "");
    }

    [RelayCommand]
    public void OpenLogFolder() => _logFolder.OpenLogFolder();

    /// <summary>Set by the composition root to show a modal Yes/No confirmation and return the
    /// choice. Kept as a callback so the shell VM stays window-agnostic (mirrors the settings seam).</summary>
    public Func<string, Task<bool>>? ConfirmClose { get; set; }

    /// <summary>Decides whether the window may close, and performs any mode-dependent teardown.
    /// Returns true to allow the close. In StartAndStopWithProgram mode this warns about active jobs
    /// and then stops the service; other modes leave the service running.</summary>
    public async Task<bool> RequestCloseAsync()
    {
        try
        {
            var settingsResult = await _gateway.GetSettingsAsync();
            // If settings can't be read (e.g. service unreachable), fall back to the default mode so a
            // transient outage never traps the user in the window.
            ServiceStartupMode mode = settingsResult.TryGetValue(out GlobalSettings? settings)
                ? settings.ServiceStartupMode
                : GlobalSettings.Default.ServiceStartupMode;

            if (mode != ServiceStartupMode.StartAndStopWithProgram)
                return true;        // leave the service running

            // Warn if the service reports work in flight. NOTE: JobsInFlight is currently hard-coded
            // to 0 by the service (real job execution is a future slice), so this guard is dormant
            // today and activates automatically once the service reports a live count.
            var statusResult = await _gateway.GetStatusAsync();
            if (statusResult.TryGetValue(out EngineStatusSnapshot? snapshot)
                && snapshot.JobsInFlight > 0
                && ConfirmClose is not null)
            {
                bool proceed = await ConfirmClose(
                    $"{snapshot.JobsInFlight} job(s) are still running. Closing now will stop the service and interrupt them. Close anyway?");
                if (!proceed)
                    return false;
            }

            // Best-effort graceful stop; log on failure but still allow the close.
            var shutdown = await _gateway.ShutdownServiceAsync();
            if (shutdown.TryGetError(out IpcError? error))
                Log.Warning("Requesting service shutdown on close failed: {Code} {Message}", error.Code, error.Message);
            return true;
        }
        catch (Exception ex)
        {
            // Last resort: never trap the user in the window because close orchestration threw.
            Log.Error(ex, "Close orchestration failed; allowing the window to close");
            return true;
        }
    }

    /// <summary>Set by the composition root to show the modal settings dialog for a prepared VM.
    /// Kept as a callback so the shell VM stays window-agnostic (mirrors the folder-picker seam).</summary>
    public Func<SettingsViewModel, Task>? ShowSettingsDialog { get; set; }

    [RelayCommand]
    public async Task OpenSettings()
    {
        if (ShowSettingsDialog is null)
            return;
        SettingsViewModel settings = new(_gateway);
        await settings.LoadAsync();
        await ShowSettingsDialog(settings);
    }

    private async Task LoadSelectionSafeAsync(ProfileListItem? item)
    {
        try
        {
            if (item is null)
            {
                Editor.Clear();
                DryRun.SetProfile(null, "");
                return;
            }
            var loaded = await _gateway.GetProfileAsync(item.ProfileId);
            if (loaded.TryGetError(out IpcError? error))
            {
                List.ErrorMessage = $"Could not open \"{item.Name}\": {error.Message}";
                Editor.Clear();
                DryRun.SetProfile(null, "");
                return;
            }
            loaded.TryGetValue(out Profile? profile);
            Editor.Load(profile!);
            DryRun.SetProfile(profile!.Id, profile.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load profile selection {ProfileId}", item?.ProfileId);
            List.ErrorMessage = $"Could not open the profile: {ex.Message}";
        }
    }

    private async Task AfterSaveAsync(Guid profileId)
    {
        try
        {
            await List.RefreshAndSelectAsync(profileId);
            DryRun.SetProfile(profileId, Editor.ProfileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Profile {ProfileId} saved, but the list failed to refresh", profileId);
            List.ErrorMessage = $"Saved, but the list failed to refresh: {ex.Message}";
        }
    }
}
