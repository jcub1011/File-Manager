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
        Activity = new ActivityViewModel(gateway);
        // The wire DTOs carry only profile ids; the list is the only place that knows the names.
        Activity.ProfileNameLookup = id => List.Profiles.FirstOrDefault(p => p.ProfileId == id)?.Name;
        Activity.HideCommand = ToggleActivityCommand;

        List.CreateProfileCommand = NewProfileCommand;
        List.ExportProfileCommand = ExportProfileCommand;
        List.RunProfileCommand = RunProfileNowCommand;
        List.DeleteProfileCommand = DeleteProfileCommand;
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
    public ActivityViewModel Activity { get; }

    /// <summary>Whether the activity panel is showing. It docks above the status bar, OUTSIDE the
    /// document area's profile gate, because engine activity is global state — not per-profile.</summary>
    [ObservableProperty]
    public partial bool ActivityVisible { get; set; }

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

    [RelayCommand]
    public void ToggleActivity()
    {
        ActivityVisible = !ActivityVisible;
        if (ActivityVisible)
            _ = Activity.ReconcileAsync();   // the event stream is lossy; re-seed on open
    }

    /// <summary>Set by the composition root to confirm a manual run before it is submitted. Mirrors
    /// <see cref="ConfirmClose"/>; a null callback proceeds, so headless tests are not blocked.</summary>
    public Func<string, Task<bool>>? ConfirmRunProfile { get; set; }

    // Run ids this window started, pending their run-queued event. Bounded: a run whose event never
    // arrives (service restart mid-scan) would otherwise leak an entry per run for the session.
    private const int MaxTrackedRuns = 32;
    private readonly HashSet<Guid> _ownRunIds = [];

    private void RememberOwnRun(Guid runId)
    {
        if (_ownRunIds.Count >= MaxTrackedRuns)
            _ownRunIds.Clear();   // the pending ones are stale by now; a missed notice beats unbounded growth
        _ownRunIds.Add(runId);
    }

    /// <summary>Submits a real run of the selected profile (spec §3.2 manual invocation). This MOVES
    /// FILES and applies the profile's source disposition, so it always confirms first, and the dialog
    /// is the only place the user sees the blast radius (source roots + disposition) spelled out.</summary>
    [RelayCommand]
    public async Task RunProfileNowAsync(ProfileListItem? item)
    {
        if (item is null)
            return;
        try
        {
            // run-profile resolves against the PERSISTED catalog, so unsaved edits would be silently
            // ignored. Refuse, reusing the editor's existing unsaved-changes affordance.
            if (Editor.IsDirty && List.UnsavedProfileId == item.ProfileId)
            {
                Editor.ShowUnsavedWarning = true;
                return;
            }

            var profileResult = await _gateway.GetProfileAsync(item.ProfileId);
            if (profileResult.IsCanceled)
                return;
            if (profileResult.TryGetError(out IpcError? loadError))
            {
                List.ErrorMessage = $"Could not run \"{item.Name}\": {loadError.Message}";
                return;
            }
            profileResult.TryGetValue(out Profile? profile);

            if (profile!.Sources.Count == 0)
            {
                List.ErrorMessage = $"\"{item.Name}\" has no sources to run.";
                return;
            }

            if (ConfirmRunProfile is not null && !await ConfirmRunProfile(BuildRunConfirmation(profile)))
                return;

            ActivityVisible = true;   // the payoff: the user watches the run land
            List.ErrorMessage = null;

            // run-profile takes ONE path, but a profile has N sources, and "Run now" means run the
            // profile — so submit one request per source root (spec §8's stance for the GUI's sibling
            // operation: never narrow the scan).
            List<string> problems = [];
            int accepted = 0;
            foreach (SourceConfig source in profile.Sources)
            {
                var run = await _gateway.RunProfileAsync(profile.Id, source.Path);
                if (run.IsCanceled)
                    return;
                if (run.TryGetError(out IpcError? runError))
                {
                    problems.Add($"{source.Path} ({runError.Message})");
                    continue;
                }
                // Remember the run id so the broadcast run-queued event can be recognized as OURS.
                run.TryGetValue(out RunProfileResponse? started);
                RememberOwnRun(started!.RunId);
                accepted++;
            }

            if (problems.Count > 0)
                List.ErrorMessage = accepted > 0
                    ? $"Started {accepted} of {profile.Sources.Count} source(s). Not started: {string.Join("; ", problems)}"
                    : $"Could not run \"{item.Name}\": {string.Join("; ", problems)}";
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a command has no exception boundary of its own.
            Log.Error(ex, "Running profile {ProfileId} failed", item.ProfileId);
            List.ErrorMessage = $"Could not run \"{item.Name}\": {ex.Message}";
        }
    }

    /// <summary>Set by the composition root to confirm a delete before it is submitted. Mirrors
    /// <see cref="ConfirmRunProfile"/>: a modal, so the same confirmation appears whether the delete
    /// came from the Profile tab, a list row, or the collapsed rail. A null callback proceeds, so
    /// headless tests are not blocked.</summary>
    public Func<string, Task<bool>>? ConfirmDeleteProfile { get; set; }

    /// <summary>Deletes a profile after confirming. Falls back to the selected profile so the Profile
    /// tab's button and the rows' right-click menu can share one command.</summary>
    [RelayCommand]
    public async Task DeleteProfileAsync(ProfileListItem? item)
    {
        item ??= List.SelectedProfile;
        if (item is null)
            return;
        if (ConfirmDeleteProfile is not null
            && !await ConfirmDeleteProfile($"Delete profile \"{item.Name}\"? This cannot be undone."))
            return;
        // Deleting a profile implies discarding an unsaved draft of it — the user just confirmed the
        // profile itself is going. Without this the list's dirty-editor navigation guard would revert
        // the post-delete deselection and leave the editor open on a profile that no longer exists.
        if (Editor.IsDirty && List.UnsavedProfileId == item.ProfileId)
            Editor.Discard();
        await List.DeleteAsync(item);
    }

    /// <summary>The confirmation text. This is the ONLY place the user learns which roots will be
    /// walked and what happens to the source files afterwards, so it names both.</summary>
    private static string BuildRunConfirmation(Profile profile)
    {
        string roots = string.Join(Environment.NewLine, profile.Sources.Select(s => "    " + s.Path));
        string disposition = profile.Policies.OnSuccess switch
        {
            OnSuccessAction.KeepSource => "the source files will be left in place",
            OnSuccessAction.MoveToArchive => "each source file will then be MOVED to the archive folder",
            OnSuccessAction.MoveToTrash => "each source file will then be MOVED TO THE RECYCLE BIN",
            OnSuccessAction.PermanentDelete => "each source file will then be PERMANENTLY DELETED",
            _ => $"the source disposition is {profile.Policies.OnSuccess}",
        };
        return $"Run \"{profile.Name}\" now?" + Environment.NewLine + Environment.NewLine
            + "This performs a REAL run. Files under:" + Environment.NewLine
            + roots + Environment.NewLine
            + $"will be copied to {profile.Targets.Count} target(s), and {disposition}.";
    }

    /// <summary>Re-seeds everything the lossy event stream cannot be trusted for. Called by the event
    /// pump on every (re)connect.</summary>
    public async Task ReconcileEngineStateAsync()
    {
        await Activity.ReconcileAsync();
        await StatusBar.PollOnceAsync();     // authoritative Paused + JobsInFlight
    }

    /// <summary>Routes one engine event to the child view models. The shell already owns every
    /// cross-view-model concern, so a separate router service would just duplicate its dependencies.</summary>
    public void HandleEngineEvent(EngineEvent evt)
    {
        if (evt is null)
            return;
        switch (evt)
        {
            case JobStartedEvent started:
                Activity.OnJobStarted(started);
                break;
            case JobProgressEvent progress:
                Activity.OnProgress(progress);
                break;
            case JobCompletedEvent completed:
                Activity.OnJobFinished(completed.Job, null, null);
                break;
            case JobFailedEvent failed:
                // failed.NotifyOnFailure is stamped for the tray's native notification (spec §7),
                // which does not exist yet — read and ignore it here.
                Activity.OnJobFinished(failed.Job, failed.Error, failed.ResidualPaths);
                break;
            case PauseChangedEvent paused:
                StatusBar.ApplyPauseChanged(paused.Paused);
                break;
            case RunQueuedEvent queued:
                // Only OUR runs. The event bus broadcasts to every subscriber, so without this the
                // window announced "Queued N file(s) from …" for a run the CLI or another client
                // started — a notice about an action this user never took.
                if (_ownRunIds.Remove(queued.RunId))
                {
                    Activity.ShowNotice(queued.Error is not null
                        ? $"Scanning {queued.ScopePath} stopped: {queued.Error} ({queued.QueuedCount} file(s) queued)"
                        : queued.QueuedCount == 0
                            ? $"Nothing in {queued.ScopePath} matched this profile."
                            : $"Queued {queued.QueuedCount} file(s) from {queued.ScopePath}.");
                }
                break;
            case EngineWarningEvent warning:
                // The activity panel's notice bar, NOT List.ErrorMessage — that danger banner is
                // reserved for things the user must act on.
                Activity.ShowNotice(warning.Message);
                break;
            case ProfilesChangedEvent:
                RefreshProfilesFromEvent();
                break;
        }
    }

    // The UI's own saves/imports/deletes mutate the catalog, so profiles-changed echoes straight back
    // and would double-refresh right after those paths already refreshed. Suppress briefly.
    private DateTimeOffset _suppressProfilesRefreshUntil = DateTimeOffset.MinValue;
    private static readonly TimeSpan ProfilesEchoWindow = TimeSpan.FromSeconds(1);

    /// <summary>Call from any path that mutates the catalog itself and refreshes on its own.</summary>
    private void SuppressNextProfilesEcho() =>
        _suppressProfilesRefreshUntil = DateTimeOffset.UtcNow + ProfilesEchoWindow;

    private void RefreshProfilesFromEvent()
    {
        if (DateTimeOffset.UtcNow < _suppressProfilesRefreshUntil)
            return;
        _ = List.RefreshAsync();
    }

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

            // Warn if the service reports work in flight. This is a live guard: the orchestrator
            // reports a real JobsInFlight count, and a started job is never suspended (I-ATOMIC-JOB),
            // so stopping the service mid-job is exactly what the user needs warning about.
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

            SuppressNextProfilesEcho();   // this path refreshes itself
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
            // This path refreshes the list itself; the service's profiles-changed echo would only
            // duplicate it.
            SuppressNextProfilesEcho();
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
