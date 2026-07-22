using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Shell state: owns the child viewmodels and wires selection → editor/dry-run,
/// save → list refresh, and the unsaved-changes navigation guard.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>Fixed width of the collapsed profile rail. Sized so the 40px avatar centres with a
    /// uniform ~8px gap on each side (40 + 8 + 8 + the 1px panel divider). Shared with the view, which
    /// snaps the sidebar column to this width when collapsed.</summary>
    public const double CollapsedSidebarWidth = 57;

    /// <summary>Smallest width the expanded sidebar may be dragged/restored to.</summary>
    public const double MinExpandedSidebarWidth = 180;

    private readonly IIpcGateway _gateway;
    private readonly IFolderPicker _folderPicker;
    private readonly ILogFolderService _logFolder;
    private readonly string _uiStatePath;

    public MainWindowViewModel(IIpcGateway gateway, IFolderPicker folderPicker, ILogFolderService logFolder,
        IDryRunItemActions dryRunActions, string? uiStatePath = null)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
        _logFolder = logFolder;
        // Test seam: tests pass an isolated path so constructing the shell VM never reads or writes
        // the developer's real %LOCALAPPDATA% ui-state file.
        _uiStatePath = uiStatePath ?? UiPaths.UiStateFilePath;
        List = new ProfileListViewModel(gateway);
        Editor = new ProfileEditorViewModel(gateway, folderPicker);
        DryRun = new DryRunViewModel(gateway, dryRunActions);
        StatusBar = new StatusBarViewModel(gateway);

        List.CreateProfileCommand = NewProfileCommand;
        List.ExportProfileCommand = ExportProfileCommand;
        List.CanNavigate = () => !Editor.IsDirty;
        List.NavigationBlocked = () => Editor.ShowUnsavedWarning = true;
        List.SelectionCommitted = item => _ = LoadSelectionSafeAsync(item);
        Editor.Saved = profileId => _ = AfterSaveAsync(profileId);

        // Dry run previews the editor's current draft (unsaved edits), so it can run without saving.
        // Discard can land on a still-open profile (revert/new) or a cleared editor — mirror that so
        // the run isn't left enabled against nothing.
        Editor.Discarded = () =>
        {
            if (Editor.HasProfile)
            {
                DryRun.SetProfile(List.SelectedProfile?.ProfileId, Editor.ProfileName);
                DryRun.ApplySyncSettings(Editor.SyncMode, Editor.ScanDestination);
            }
            else
                DryRun.ClearProfile();
        };
        DryRun.DraftProvider = () =>
            Editor.TryBuildDraft(out Profile? draft, out string? error) ? (draft, null) : ((Profile?)null, error);
        // Keep the sidebar's unsaved-changes marker and row-locking in step with the editor's dirty
        // state: while dirty, other rows become unselectable so the user can't appear to navigate away.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileEditorViewModel.IsDirty))
            {
                List.HasUnsavedChanges = Editor.IsDirty;
                List.UnsavedProfileId = Editor.IsDirty ? List.SelectedProfile?.ProfileId : null;
            }
            // Keep the dry-run split button's default scan choice in step with the editor's live
            // sync mode / ScanDestination (a dropdown override is transient and reset on any change).
            else if (e.PropertyName is nameof(ProfileEditorViewModel.SyncMode)
                     or nameof(ProfileEditorViewModel.ScanDestination))
            {
                DryRun.ApplySyncSettings(Editor.SyncMode, Editor.ScanDestination);
            }
        };

        // Restore the persisted sidebar layout (collapsed state + expanded width). The view applies
        // the column geometry from these once its template is loaded.
        UiState ui = UiStateStore.Read(_uiStatePath);
        SidebarCollapsed = ui.SidebarCollapsed;
        SidebarExpandedWidth = Math.Max(MinExpandedSidebarWidth, ui.SidebarWidth);
    }

    public ProfileListViewModel List { get; }
    public ProfileEditorViewModel Editor { get; }
    public DryRunViewModel DryRun { get; }
    public StatusBarViewModel StatusBar { get; }

    /// <summary>Whether the profile sidebar is collapsed to the narrow icon rail. The view toggles
    /// this (double-tap on the splitter) and switches the sidebar content off it.</summary>
    [ObservableProperty]
    public partial bool SidebarCollapsed { get; set; }

    /// <summary>The expanded sidebar width to restore when un-collapsing. The view keeps this in step
    /// with the live column width (on collapse and on close) and persists it via
    /// <see cref="SaveSidebarState"/>.</summary>
    public double SidebarExpandedWidth { get; set; } = 280;

    /// <summary>Persist the current sidebar layout. Single entry point called by the view whenever the
    /// collapsed state or expanded width changes.</summary>
    public void SaveSidebarState() =>
        UiStateStore.Write(_uiStatePath, new UiState(SidebarCollapsed, SidebarExpandedWidth));

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
        DryRun.SetProfile(null, Editor.ProfileName);   // new/unsaved profile is still dry-runnable
        DryRun.ApplySyncSettings(Editor.SyncMode, Editor.ScanDestination);
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
        try
        {
            if (ShowSettingsDialog is null)
                return;
            SettingsViewModel settings = new(_gateway, _folderPicker)
            {
                // A relocation changes which profiles the service serves, so refresh the list to reflect
                // the new directory's contents without waiting for the user to hit refresh.
                ProfilesRelocated = () => List.RefreshAsync(),
            };
            await settings.LoadAsync();
            await ShowSettingsDialog(settings);
        }
        catch (Exception ex)
        {
            // Last resort: a dialog failure must be logged, never an unhandled UI-thread crash.
            Log.Error(ex, "Opening the settings dialog failed unexpectedly");
            List.ErrorMessage = $"Could not open settings: {ex.Message}";
        }
    }

    /// <summary>Exports a single profile (the right-clicked row) to a chosen folder as one .json file.</summary>
    [RelayCommand]
    private async Task ExportProfile(ProfileListItem? item)
    {
        if (item is null)
            return;
        try
        {
            string? folder = await _folderPicker.PickFolderAsync($"Choose a folder to export \"{item.Name}\"");
            if (folder is null)
                return;

            var loaded = await _gateway.GetProfileAsync(item.ProfileId);
            if (loaded.TryGetError(out IpcError? error))
            {
                List.ErrorMessage = $"Could not export \"{item.Name}\": {error.Message}";
                return;
            }
            loaded.TryGetValue(out Profile? profile);

            string fileName = ProfileExport.UniqueFileName(
                profile!.Name, folder, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            ProfileExport.WriteAtomic(Path.Combine(folder, fileName), profile);
            List.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exporting profile {ProfileId} failed unexpectedly", item.ProfileId);
            List.ErrorMessage = $"Could not export \"{item.Name}\": {ex.Message}";
        }
    }

    /// <summary>Set by the composition root to show the modal export dialog for a prepared VM.
    /// Kept as a callback so the shell VM stays window-agnostic (mirrors the settings seam).</summary>
    public Func<ExportProfilesViewModel, Task>? ShowExportDialog { get; set; }

    [RelayCommand]
    public async Task ExportProfiles()
    {
        try
        {
            if (ShowExportDialog is null)
                return;
            ExportProfilesViewModel export = new(_gateway, _folderPicker);
            await export.LoadAsync();
            await ShowExportDialog(export);
        }
        catch (Exception ex)
        {
            // Last resort: a dialog failure must be logged, never an unhandled UI-thread crash.
            Log.Error(ex, "Opening the export dialog failed unexpectedly");
            List.ErrorMessage = $"Could not open the export dialog: {ex.Message}";
        }
    }

    /// <summary>Set by the composition root to show the modal per-profile import preview and return
    /// the user's Import/Skip choice. Null (headless/tests) imports without a preview — the
    /// validator's blocking warnings remain the safety net there.</summary>
    public Func<ImportPreviewViewModel, Task<bool>>? ConfirmImport { get; set; }

    /// <summary>Imports profile files as new copies: each keeps its settings but gets a fresh id and a
    /// "(imported)" name, and comes in inactive so it never silently starts syncing or collides with an
    /// active profile on import. Collisions with existing ids are therefore impossible. Before saving,
    /// each profile's sources/targets/disposition/transformers are shown for confirmation — the file
    /// was authored outside this app, so the user must see what it would touch.</summary>
    [RelayCommand]
    public async Task ImportProfiles()
    {
        try
        {
            IReadOnlyList<string> files = await _folderPicker.PickFilesAsync("Choose profile files to import");
            if (files.Count == 0)
                return;

            int imported = 0;
            List<string> problems = [];
            foreach (string file in files)
            {
                Profile? profile;
                try
                {
                    using FileStream stream = File.OpenRead(file);
                    profile = JsonSerializer.Deserialize(stream, FileManagerJsonContext.Default.Profile);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                    continue;
                }
                catch (Exception ex)
                {
                    // Last resort: one unreadable file must not abort the rest of the batch.
                    Log.Error(ex, "Importing {File} failed unexpectedly", file);
                    problems.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (profile is null)
                {
                    problems.Add($"{Path.GetFileName(file)}: not a valid profile file");
                    continue;
                }

                if (ConfirmImport is not null
                    && !await ConfirmImport(ImportPreviewViewModel.From(Path.GetFileName(file), profile)))
                    continue;   // skipped by the user — deliberate, so not a "problem"

                Profile copy = profile with
                {
                    Id = Guid.NewGuid(),
                    Name = profile.Name + " (imported)",
                    Active = false,
                };
                var saved = await _gateway.SaveProfileAsync(copy, acknowledgeWarnings: false);
                if (saved.TryGetError(out IpcError? error))
                {
                    problems.Add($"{Path.GetFileName(file)}: {error.Message}");
                    continue;
                }
                saved.TryGetValue(out SaveOutcome? outcome);
                if (!outcome!.Saved)
                {
                    string codes = string.Join(", ", outcome.Issues.Select(i => i.Code));
                    problems.Add($"{Path.GetFileName(file)}: not imported ({codes})");
                    continue;
                }
                imported++;
            }

            await List.RefreshAsync();
            // The list banner is danger-styled, so only raise it when something needs the user's
            // attention; a clean import is evident from the new rows appearing in the list.
            List.ErrorMessage = problems.Count > 0
                ? $"Imported {imported} of {files.Count} file(s). Not imported: {string.Join("; ", problems)}"
                : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Importing profiles failed unexpectedly");
            List.ErrorMessage = $"Import failed unexpectedly: {ex.Message}";
        }
    }

    private async Task LoadSelectionSafeAsync(ProfileListItem? item)
    {
        try
        {
            if (item is null)
            {
                Editor.Clear();
                DryRun.ClearProfile();
                return;
            }
            var loaded = await _gateway.GetProfileAsync(item.ProfileId);
            if (loaded.TryGetError(out IpcError? error))
            {
                List.ErrorMessage = $"Could not open \"{item.Name}\": {error.Message}";
                Editor.Clear();
                DryRun.ClearProfile();
                return;
            }
            loaded.TryGetValue(out Profile? profile);
            Editor.Load(profile!);
            DryRun.SetProfile(profile!.Id, profile.Name);
            DryRun.ApplySyncSettings(profile.SyncMode, profile.ScanDestination);
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
            DryRun.ApplySyncSettings(Editor.SyncMode, Editor.ScanDestination);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Profile {ProfileId} saved, but the list failed to refresh", profileId);
            List.ErrorMessage = $"Saved, but the list failed to refresh: {ex.Message}";
        }
    }
}
