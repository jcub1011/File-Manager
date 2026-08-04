using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
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
    private readonly ISystemDrives? _systemDrives;
    private readonly string _clientSettingsPath;

    public MainWindowViewModel(IIpcGateway gateway, IFolderPicker folderPicker, ILogFolderService logFolder,
        IDryRunItemActions dryRunActions, string? clientSettingsPath = null, ISystemDrives? systemDrives = null)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
        _logFolder = logFolder;
        // Only used when the settings dialog opens; null lets SettingsViewModel fall back to the real
        // enumeration, so tests that never open settings need not supply one.
        _systemDrives = systemDrives;
        // Test seam: tests pass an isolated path so constructing the shell VM never reads or writes
        // the developer's real %LOCALAPPDATA% client-settings file.
        _clientSettingsPath = clientSettingsPath ?? UiPaths.ClientSettingsFilePath;
        List = new ProfileListViewModel(gateway);
        Editor = new ProfileEditorViewModel(gateway, folderPicker);
        DryRun = new DryRunViewModel(gateway, dryRunActions);
        StatusBar = new StatusBarViewModel(gateway);
        Activity = new ActivityViewModel(gateway);
        // A degraded engine startup, learned from the status poll rather than from the engine-warning
        // event — the event is published before any client can have subscribed. Same sink as the event
        // so the two are indistinguishable to the user, and the poll's own de-duplication keeps it from
        // being re-announced every 2 seconds.
        StatusBar.StartupWarningObserved = message => Activity.ShowNotice(message);
        // The wire DTOs carry only profile ids; the list is the only place that knows the names.
        Activity.ProfileNameLookup = id => List.Profiles.FirstOrDefault(p => p.ProfileId == id)?.Name;
        Activity.HideCommand = ToggleActivityCommand;

        List.CreateProfileCommand = NewProfileCommand;
        List.ExportProfileCommand = ExportProfileCommand;
        List.RunProfileCommand = PreviewProfileCommand;
        List.DeleteProfileCommand = DeleteProfileCommand;
        List.CanNavigate = () => !Editor.IsDirty;
        List.NavigationBlocked = () => Editor.ShowUnsavedWarning = true;
        // Kept rather than discarded so a caller that MOVES the selection can await the load it started
        // — PreviewProfileAsync must not plan a draft the editor has not finished loading.
        List.SelectionCommitted = item => _selectionLoad = LoadSelectionSafeAsync(item);
        Editor.Saved = profileId => _ = AfterSaveAsync(profileId);

        // A preview plans the editor's current draft (unsaved edits), so it runs without saving.
        // Discard can land on a still-open profile (revert/new) or a cleared editor — mirror that so
        // the run isn't left enabled against nothing.
        Editor.Discarded = () =>
        {
            if (Editor.HasProfile)
            {
                DryRun.SetProfile(List.SelectedProfile?.ProfileId, Editor.ProfileName);
                DryRun.ApplySyncMode(Editor.SyncMode);
            }
            else
                DryRun.ClearProfile();
        };
        DryRun.DraftProvider = () =>
            Editor.TryBuildDraft(out Profile? draft, out string? error) ? (draft, null) : ((Profile?)null, error);
        DryRun.RunAnswered = approved =>
        {
            if (approved)
                ActivityVisible = true;   // the payoff: the user watches the run land
            Activity.ShowNotice(approved
                ? "Run approved — starting now."
                : "Run discarded — nothing was changed.");
        };
        // Keep the sidebar's unsaved-changes marker and row-locking in step with the editor's dirty
        // state: while dirty, other rows become unselectable so the user can't appear to navigate away.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileEditorViewModel.IsDirty))
            {
                List.HasUnsavedChanges = Editor.IsDirty;
                List.UnsavedProfileId = Editor.IsDirty ? List.SelectedProfile?.ProfileId : null;
            }
            // Keep the Preview footer's Mirror warning in step with the editor's live sync mode, so it
            // describes the profile on screen rather than the one the last plan was built from.
            else if (e.PropertyName == nameof(ProfileEditorViewModel.SyncMode))
            {
                DryRun.ApplySyncMode(Editor.SyncMode);
            }
        };

        // Restore the persisted sidebar layout (collapsed state + expanded width). The view applies
        // the column geometry from these once its template is loaded.
        ClientSettings client = ClientSettingsStore.Read(_clientSettingsPath);
        SidebarCollapsed = client.SidebarCollapsed;
        SidebarExpandedWidth = Math.Max(MinExpandedSidebarWidth, client.SidebarWidth);
    }

    public ProfileListViewModel List { get; }
    public ProfileEditorViewModel Editor { get; }
    public DryRunViewModel DryRun { get; }
    public StatusBarViewModel StatusBar { get; }
    public ActivityViewModel Activity { get; }

    /// <summary>The Profile tab's index in the document TabControl.</summary>
    public const int ProfileTabIndex = 0;

    /// <summary>The Preview tab's index in the document TabControl.</summary>
    public const int PreviewTabIndex = 1;

    /// <summary>Which document tab is showing. Two-way bound, because a preview NAVIGATES: the user
    /// presses Preview on the Profile tab and the rows they asked for appear on the Preview tab, which
    /// only works if the view model can move the selection.</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

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
    /// collapsed state or expanded width changes. Read-modify-write, not a fresh record: the settings
    /// window owns the other half of this file and saves on its own schedule.</summary>
    public void SaveSidebarState() =>
        ClientSettingsStore.Update(_clientSettingsPath, stored => stored with
        {
            SidebarCollapsed = SidebarCollapsed,
            SidebarWidth = SidebarExpandedWidth,
        });

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
        DryRun.SetProfile(null, Editor.ProfileName);   // a never-saved profile is still previewable
        DryRun.ApplySyncMode(Editor.SyncMode);
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

    // Run ids this window started, pending their run-queued event. Bounded: a run whose event never
    // arrives (service restart mid-scan) would otherwise leak an entry per run for the session.
    private const int MaxTrackedRuns = 32;
    private readonly HashSet<Guid> _ownRunIds = [];

    // The in-flight editor load started by the last selection change, so a caller that moved the
    // selection itself can await it. Never null: an un-awaited completed task is the no-op case.
    private Task _selectionLoad = Task.CompletedTask;

    private void RememberOwnRun(Guid runId)
    {
        if (_ownRunIds.Count >= MaxTrackedRuns)
            _ownRunIds.Clear();   // the pending ones are stale by now; a missed notice beats unbounded growth
        _ownRunIds.Add(runId);
    }

    /// <summary>Previews what the selected profile will do, by starting the PLANNING phase of a real run
    /// and showing its frozen work list on the Preview tab.
    ///
    /// <para>There is no confirmation dialog here and nothing to confirm: planning is read-only by
    /// construction, and the run parks in <c>AwaitingApproval</c> until the Preview tab's footer answers
    /// it. That footer — showing the actual rows — is what the old blast-radius dialog was standing in
    /// for.</para>
    ///
    /// <para>The run plans the editor's DRAFT, so what is previewed is what is on screen, unsaved edits
    /// included. The draft is frozen into the run's snapshot and the copies execute against it, so
    /// approving cannot run a different profile than the one previewed.</para></summary>
    [RelayCommand]
    public async Task PreviewProfileAsync(ProfileListItem? item)
    {
        // Null covers two callers: the Profile tab's button before a row is selected, and the Preview
        // tab's own button, which previews whatever the editor already holds.
        string name = item?.Name ?? List.SelectedProfile?.Name ?? Editor.ProfileName;
        try
        {
            // A row's menu can name a profile other than the one open in the editor, and the draft is
            // the only thing a preview plans — so open it first, through the list's selection, which is
            // what applies the unsaved-changes guard. A refused navigation leaves the editor's edits
            // alone and previews nothing, exactly as the old command refused to run a dirty profile.
            if (item is not null && item.ProfileId != List.SelectedProfile?.ProfileId)
            {
                List.SelectedProfile = item;
                if (List.SelectedProfile?.ProfileId != item.ProfileId)
                    return;
                await _selectionLoad;   // the load that selection just started
            }

            // The draft IS the profile to plan. A parse error is the editor's to report, and it stops
            // this before the tab switches — a Preview tab showing nothing is worse than staying put.
            if (!Editor.TryBuildDraft(out Profile? draft, out string? buildError))
            {
                List.ErrorMessage = buildError;
                return;
            }
            if (draft is null)
                return;
            if (draft.Sources.Count == 0)
            {
                List.ErrorMessage = $"\"{name}\" has no sources to run.";
                return;
            }

            List.ErrorMessage = null;
            SelectedTabIndex = PreviewTabIndex;
            // Clears the previous preview and declines the run it was holding, BEFORE this one exists —
            // so at most one run is ever parked awaiting this window's approval.
            DryRun.BeginPlanning();

            // ONE request for the whole profile. This used to be one per source root, which is wrong
            // for Mirror: an orphan is "a destination file no source writes to", so the decision can
            // only be made over the complete source set — N independent runs would each see the other
            // sources' files as orphans.
            // draft.Id, not the list row's: for a never-saved profile they are the same value and there
            // is no row, and for a saved one the draft carries the persisted id anyway.
            var run = await _gateway.RunProfileAsync(draft.Id, path: null, draft: draft);
            if (run.IsCanceled)
            {
                DryRun.EndPlanning();
                return;
            }
            if (run.TryGetError(out IpcError? runError))
            {
                DryRun.EndPlanning($"Could not preview \"{name}\": {runError.Message}");
                return;
            }
            run.TryGetValue(out RunProfileResponse? started);
            // Remembered so the broadcast run-planned/run-completed events can be recognized as OURS —
            // and, for run-planned, so this window is the one that shows the plan.
            RememberOwnRun(started!.RunId);
            // Makes the planning scan cancellable: a large profile's plan is minutes of walking, and the
            // user must be able to stop it.
            DryRun.PlanningStarted(started.RunId);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a command has no exception boundary of its own.
            Log.Error(ex, "Previewing profile \"{Name}\" failed", name);
            DryRun.EndPlanning($"Could not preview \"{name}\": {ex.Message}");
        }
    }

    /// <summary>Set by the composition root to confirm a delete before it is submitted. Mirrors
    /// <see cref="ConfirmClose"/>: a modal, so the same confirmation appears whether the delete came
    /// from the Profile tab, a list row, or the collapsed rail. A null callback proceeds, so headless
    /// tests are not blocked.</summary>
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

    /// <summary>Shows a finished plan on the Preview tab, where its footer asks for the approval.
    ///
    /// <para>This is the moment the two-phase run exists for: the work list is frozen, nothing has been
    /// touched, and what is displayed is what will happen. Two plans never reach the view — one that
    /// failed outright (nothing to approve) and one with nothing to do (nothing worth approving); both
    /// are answered here so no run is left parked holding a snapshot.</para></summary>
    private async Task ShowRunPlanAsync(RunPlannedEvent planned)
    {
        try
        {
            if (planned.Error is { } planError)
            {
                _ownRunIds.Remove(planned.RunId);
                DryRun.EndPlanning($"Could not work out what to run: {planError}");
                return;
            }
            if (planned.PlannedCopies == 0 && planned.PlannedDeletes == 0)
            {
                _ownRunIds.Remove(planned.RunId);
                DryRun.EndPlanning(emptyState: "Nothing to do — everything is already up to date.");
                await _gateway.ApproveRunAsync(planned.RunId, approve: false);
                Activity.ShowNotice("Nothing to do — everything is already up to date.");
                return;
            }

            await DryRun.LoadPlanAsync(planned);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): this is a fire-and-forget continuation off the event
            // pump, so it has no exception boundary of its own.
            Log.Error(ex, "Showing the plan for run {RunId} failed", planned.RunId);
            _ownRunIds.Remove(planned.RunId);
            DryRun.EndPlanning($"Could not show what the run will do: {ex.Message}");
        }
    }

    private static string DescribeRunOutcome(RunCompletedEvent completed)
    {
        string counts = $"{completed.Succeeded} copied, {completed.Skipped} skipped";
        if (completed.Failed > 0)
            counts += $", {completed.Failed} FAILED";
        if (completed.Deleted > 0)
            counts += $", {completed.Deleted} removed ({ByteSize.Format(completed.BytesDeleted)})";
        string text = $"Run finished ({completed.Outcome}): {counts}.";
        if (completed.DeletionAbortReason is { } reason)
            text += $" No files were removed from the targets — {reason}";
        return text;
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
            case RunPlannedEvent planned:
                // Only OUR run: the bus is a broadcast, and a second window must not show — let alone be
                // asked to approve — work this user did not start. The id stays remembered until the run
                // closes, because run-completed needs it too.
                if (_ownRunIds.Contains(planned.RunId))
                    _ = ShowRunPlanAsync(planned);
                break;
            case RunProgressEvent runProgress:
                if (_ownRunIds.Contains(runProgress.RunId))
                    Activity.ShowNotice(
                        $"Running: {runProgress.Completed} of {runProgress.Total} file(s)"
                        + (runProgress.Deleted > 0 ? $", {runProgress.Deleted} removed" : "") + "…");
                break;
            case RunCompletedEvent runCompleted:
                if (_ownRunIds.Remove(runCompleted.RunId))
                    Activity.ShowNotice(DescribeRunOutcome(runCompleted));
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
            // Whatever the mode, a preview left on screen is a run parked in AwaitingApproval holding a
            // snapshot directory that nothing will answer for once this window is gone. Decline it first
            // — before the branch below can return early and leave it stranded.
            DryRun.AbandonPendingRun();

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
            SettingsViewModel settings = new(_gateway, _folderPicker, _systemDrives)
            {
                // A relocation changes which profiles the service serves, so refresh the list to reflect
                // the new directory's contents without waiting for the user to hit refresh.
                ProfilesRelocated = () => List.RefreshAsync(),
            };
            // Start the load but do NOT await it before showing: it makes an IPC call, and when the
            // service is unreachable that call can take the launcher's full retry budget — repeatedly,
            // queued behind the status poll on the same connect gate. Awaiting it here is what made an
            // unreachable service lock the user out of the one window that can fix it. LoadAsync turns
            // every failure into a banner rather than throwing, populates the client-side settings
            // before its first await, and never writes the editable state again on the failure path, so
            // a late completion cannot clobber what the user has started typing.
            Task loading = settings.LoadAsync();
            await ShowSettingsDialog(settings);
            await loading;      // observe it; the window has already closed by now
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
            DryRun.ApplySyncMode(profile.SyncMode);
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
            DryRun.ApplySyncMode(Editor.SyncMode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Profile {ProfileId} saved, but the list failed to refresh", profileId);
            List.ErrorMessage = $"Saved, but the list failed to refresh: {ex.Message}";
        }
    }
}
