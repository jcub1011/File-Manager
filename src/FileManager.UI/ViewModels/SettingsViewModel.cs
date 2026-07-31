using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using FileManager.UI.Undo;
using FileManager.UI.ViewModels.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Edits the machine-level <see cref="GlobalSettings"/> — theme, service startup, and the
/// scan/hash thread budgets (<see cref="ScanThreadingSettings"/>). Loads from and saves to the service
/// over IPC. Scan concurrency is global-only now: profiles no longer override it.
///
/// Settings are declared as a catalog of <see cref="SettingItemViewModel"/>s (see
/// <see cref="BuildCatalog"/>) rather than as loose properties, so the search box, the navigation tree,
/// the scroll anchors, and undo/redo are all generated from one declaration. Adding a setting means: a
/// field, one line in <see cref="BuildCatalog"/>, and a line each in <see cref="LoadAsync"/> and
/// <see cref="SaveAsync"/> — no view changes unless it needs a brand-new editor kind, and no undo
/// wiring at all.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;
    private readonly IFolderPicker _folderPicker;

    public SettingsViewModel(IIpcGateway gateway, IFolderPicker folderPicker, ISystemDrives? drives = null)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;

        Theme = Setting.Choice(
            "appearance.theme", "Theme",
            "The application theme. System follows the Windows light/dark preference; Light and Dark force a fixed theme.",
            [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark],
            ["appearance", "colour", "color", "dark mode", "light mode"]);

        StartupMode = Setting.Choice(
            "startup.serviceMode", "Startup mode",
            "When the background service runs. Run on Startup launches it at Windows login. Start on Program Open starts it when this app opens and leaves it running. Start and Stop with Program starts it on open and stops it when the app closes (warning first if jobs are running).",
            [ServiceStartupMode.RunOnStartup, ServiceStartupMode.StartOnProgramOpen, ServiceStartupMode.StartAndStopWithProgram],
            ["service", "background", "autostart", "auto start", "login", "boot", "windows"]);

        MaxScanThreads = Setting.AutoNumber(
            "performance.maxScanThreads", "Max scan threads",
            $"Caps the total number of directory-enumeration threads across all profiles. Auto uses cores × 8 ({ScanAutoDefault} on this machine).",
            ["performance", "concurrency", "parallel", "cpu", "workers", "enumeration", "threads"]);

        PerDriveDefault = Setting.AutoNumber(
            "performance.perDriveDefault", "Per-drive default",
            $"Caps the enumeration threads used on any one drive. Auto uses cores × 4 ({PerDriveAutoDefault} on this machine).",
            ["performance", "concurrency", "parallel", "cpu", "workers", "disk", "volume", "threads"]);

        MaxHashThreads = Setting.AutoNumber(
            "performance.maxHashThreads", "Max hash threads",
            $"Caps parallel file hashing during a dry run. Auto uses cores − 1 ({HashAutoDefault} on this machine).",
            ["performance", "concurrency", "parallel", "cpu", "workers", "hash", "checksum", "verify", "threads"]);

        DriveOverrides = new DriveOverridesSettingViewModel(
            "performance.driveOverrides", "Per-drive overrides",
            "Override the per-drive budget for a whole drive type, or for a specific volume key (highest precedence). Precedence: specific drive → drive type → per-drive default.",
            PerDriveAutoDefault,
            drives ?? new SystemDrives(),
            ["performance", "advanced", "drive", "disk", "volume", "network", "removable", "optical", "override"]);

        ScratchDirectory = Setting.Text(
            "storage.scratchDirectory", "Dry-run scratch directory",
            "Where the service spills a large dry run's findings to disk before streaming them to this window. Small runs stay in memory and never touch it. Leftover snapshots are purged on service startup.",
            ["storage", "folder", "directory", "path", "temp", "spill", "dry run", "location"],
            actionButtonText: "Browse…", actionCommand: BrowseScratchDirectoryCommand);

        ProfilesDirectory = Setting.Text(
            "storage.profilesDirectory", "Profiles storage",
            "Where profile files are stored. Change… applies immediately: it asks whether to move the existing profiles into the new folder (defaulting to no), then switches the service to it.",
            ["storage", "folder", "directory", "path", "profiles", "location", "move"],
            readOnly: true,
            actionButtonText: "Change…", actionCommand: ChangeProfilesDirectoryCommand,
            // Change… relocates files on disk immediately, so there is nothing to undo and nothing left
            // pending for Save. Tracking it would also make the window read as dirty right after a
            // relocation that already succeeded.
            undoable: false);

        ScratchDirectory.Value = GlobalSettings.DefaultScratchDirectory;
        ProfilesDirectory.Value = GlobalSettings.DefaultProfilesDirectory;
        MaxScanThreads.Value = ScanAutoDefault;
        PerDriveDefault.Value = PerDriveAutoDefault;
        MaxHashThreads.Value = HashAutoDefault;

        BuildCatalog();
        BuildNavigation();
        TrackForUndo();      // last: the recorder caches the values the catalog was built with
    }

    // Shadow "explicit" defaults shown when a budget's Auto is unchecked, matching the engine's auto
    // formulas so the starting number is the value auto would have chosen.
    private static int ScanAutoDefault => Environment.ProcessorCount * 8;
    private static int HashAutoDefault => Math.Max(1, Environment.ProcessorCount - 1);
    private static int PerDriveAutoDefault => Environment.ProcessorCount * 4;

    // ============================ The settings themselves ============================
    // Held as strongly-typed handles so the load/save mapping and the tests stay type-safe; the view
    // never touches these directly, it renders Categories.

    public ChoiceSettingViewModel<ThemeMode> Theme { get; }
    public ChoiceSettingViewModel<ServiceStartupMode> StartupMode { get; }
    public AutoNumberSettingViewModel MaxScanThreads { get; }
    public AutoNumberSettingViewModel PerDriveDefault { get; }
    public AutoNumberSettingViewModel MaxHashThreads { get; }
    public DriveOverridesSettingViewModel DriveOverrides { get; }
    public TextSettingViewModel ScratchDirectory { get; }
    public TextSettingViewModel ProfilesDirectory { get; }

    /// <summary>The document: every category in display order. Categories and their settings are fixed
    /// once built; searching toggles <c>IsVisible</c> rather than rebuilding, so nothing loses focus
    /// mid-edit.</summary>
    public ObservableCollection<SettingsCategoryViewModel> Categories { get; } = [];

    /// <summary>The left-hand navigation tree, mirroring <see cref="Categories"/>.</summary>
    public ObservableCollection<SettingsNavNodeViewModel> NavNodes { get; } = [];

    /// <summary>Declares the categories and the settings in each. This is the only place a new setting
    /// has to be registered for it to appear in the document, the navigation tree, and the search.</summary>
    private void BuildCatalog()
    {
        SettingsCategoryViewModel performance = new(
            "performance", "Scan performance", "Thread budgets shared across all profiles.");

        Categories.Add(new SettingsCategoryViewModel("appearance", "Appearance").With(Theme));
        Categories.Add(new SettingsCategoryViewModel("startup", "Service startup").With(StartupMode));
        Categories.Add(performance.With(MaxScanThreads, PerDriveDefault, MaxHashThreads));
        Categories.Add(new SettingsCategoryViewModel("performance.advanced", "Per-drive overrides", parent: performance)
            .With(DriveOverrides));
        Categories.Add(new SettingsCategoryViewModel("storage", "Storage").With(ScratchDirectory, ProfilesDirectory));
    }

    /// <summary>Builds the navigation tree from <see cref="Categories"/>: one branch per top-level
    /// category with its settings as leaves, and nested categories hanging off their parent's branch.</summary>
    private void BuildNavigation()
    {
        Dictionary<string, SettingsNavNodeViewModel> byCategoryId = [];

        foreach (SettingsCategoryViewModel category in Categories)
        {
            SettingsNavNodeViewModel node = SettingsNavNodeViewModel.ForCategory(category);
            byCategoryId[category.Id] = node;

            foreach (SettingItemViewModel item in category.Items)
                node.Children.Add(SettingsNavNodeViewModel.ForSetting(item));

            // A nested category becomes a child branch of its parent. Parents are declared before their
            // children in BuildCatalog, so the lookup always succeeds; an out-of-order declaration
            // degrades to a top-level branch rather than losing the node.
            if (category.Parent is not null && byCategoryId.TryGetValue(category.Parent.Id, out SettingsNavNodeViewModel? parentNode))
                parentNode.Children.Add(node);
            else
                NavNodes.Add(node);
        }
    }

    // ============================ Undo / redo and unsaved changes ============================

    /// <summary>Undo/redo for this editing session, and the source of <see cref="IsDirty"/>. Bound
    /// directly by the footer's Undo/Redo buttons; the keyboard shortcuts run the same commands.</summary>
    public UndoHistory History { get; } = new();

    /// <summary>Registers every reversible setting. One loop over the catalog, so a newly declared
    /// setting is undoable with no extra wiring — the only opt-out is
    /// <see cref="SettingItemViewModel.IsUndoable"/>.</summary>
    private void TrackForUndo()
    {
        foreach (SettingsCategoryViewModel category in Categories)
        {
            foreach (SettingItemViewModel item in category.Items)
            {
                if (item is IUndoTrackable trackable && item.IsUndoable)
                    History.Track(trackable);
            }
        }
        History.PropertyChanged += OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoHistory.IsDirty))
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CloseButtonText));
        }
    }

    /// <summary>True when there are edits the Save button has not persisted.</summary>
    public bool IsDirty => History.IsDirty;

    /// <summary>What the close button says. Naming the consequence at the moment of the click is the
    /// whole point: closing has always discarded pending edits, it just never said so.</summary>
    public string CloseButtonText => IsDirty ? "Discard changes" : "Close";

    // ============================ Search ============================

    /// <summary>The search query. Filtering runs synchronously on every keystroke — unlike
    /// <see cref="ProfileListViewModel"/> and <see cref="DryRunViewModel"/>, which debounce because
    /// their filters run over IPC-sized collections, the catalog is a few dozen in-memory items whose
    /// haystacks are pre-built, so a debounce would only add latency.</summary>
    [ObservableProperty] public partial string? SearchText { get; set; }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    /// <summary>False when the query matches nothing, which shows the empty-state message instead of a
    /// blank document.</summary>
    [ObservableProperty] public partial bool HasVisibleSettings { get; set; } = true;

    [RelayCommand] private void ClearSearch() => SearchText = null;

    /// <summary>Applies <see cref="SearchText"/> to the document and mirrors the result onto the
    /// navigation tree. An empty query makes everything visible again.</summary>
    private void ApplyFilter()
    {
        string[] terms = SettingsSearch.Terms(SearchText);
        bool searching = terms.Length > 0;
        bool anyVisible = false;

        foreach (SettingsCategoryViewModel category in Categories)
        {
            bool categoryVisible = false;
            foreach (SettingItemViewModel item in category.Items)
            {
                bool visible = !searching || SettingsSearch.Matches(item.Haystack, terms);
                item.IsVisible = visible;
                categoryVisible |= visible;
            }
            category.IsVisible = categoryVisible;
            anyVisible |= categoryVisible;
        }

        SyncNavigationVisibility(NavNodes, searching);
        HasVisibleSettings = anyVisible;
    }

    /// <summary>Mirrors item/category visibility onto the tree, bottom-up so a branch survives when only
    /// a nested category matched. While a query is active every surviving branch is expanded, so matches
    /// are visible without a click.</summary>
    private static bool SyncNavigationVisibility(IEnumerable<SettingsNavNodeViewModel> nodes, bool searching)
    {
        bool anyVisible = false;
        foreach (SettingsNavNodeViewModel node in nodes)
        {
            bool childVisible = SyncNavigationVisibility(node.Children, searching);
            bool selfVisible = node.Setting?.IsVisible ?? node.Category?.IsVisible ?? true;
            node.IsVisible = selfVisible || childVisible;
            if (searching && node.IsVisible)
                node.IsExpanded = true;
            anyVisible |= node.IsVisible;
        }
        return anyVisible;
    }

    // ============================ Navigation ============================

    /// <summary>The tree's selection. Setting it (from the tree, or programmatically) marks the target
    /// setting selected and asks the view to scroll it into view.</summary>
    [ObservableProperty] public partial SettingsNavNodeViewModel? SelectedNavNode { get; set; }

    partial void OnSelectedNavNodeChanged(SettingsNavNodeViewModel? value)
    {
        if (value is null)
            return;
        SettingItemViewModel? target = value.Setting ?? FirstVisibleSetting(value);
        if (target is not null)
            SelectSetting(target);
    }

    /// <summary>The setting the tree last navigated to, or null before any navigation.</summary>
    public SettingItemViewModel? SelectedSetting { get; private set; }

    private void SelectSetting(SettingItemViewModel item)
    {
        // Jumping elsewhere ends whatever the user was typing, so the next edit starts its own undo step
        // rather than being folded into an edit they have already moved on from.
        History.BreakMerge();
        if (SelectedSetting is not null && !ReferenceEquals(SelectedSetting, item))
            SelectedSetting.IsSelected = false;
        SelectedSetting = item;
        item.IsSelected = true;
        ScrollToRequested?.Invoke(this, item);
    }

    /// <summary>Raised when a setting should be brought into view. Handled by the window, because
    /// scrolling is a view concern and the VM stays window-agnostic (mirrors <see cref="RequestClose"/>).</summary>
    public event EventHandler<SettingItemViewModel>? ScrollToRequested;

    /// <summary>The first visible setting under a category branch, so selecting a category jumps to the
    /// top of that section even when a search has hidden its leading settings.</summary>
    private static SettingItemViewModel? FirstVisibleSetting(SettingsNavNodeViewModel node)
    {
        if (node.Category is not null)
        {
            foreach (SettingItemViewModel item in node.Category.Items)
            {
                if (item.IsVisible)
                    return item;
            }
        }
        foreach (SettingsNavNodeViewModel child in node.Children)
        {
            SettingItemViewModel? found = child.Setting is { IsVisible: true } leaf ? leaf : FirstVisibleSetting(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    // ============================ Shell state ============================

    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeProfilesDirectoryCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task BrowseScratchDirectory()
    {
        string? picked = await _folderPicker.PickFolderAsync(
            "Choose the dry-run scratch directory", ScratchDirectory.Value);
        if (picked is not null)
            ScratchDirectory.Value = picked;
    }

    /// <summary>Set by the host to ask (modal Yes/No, defaulting to No) whether the existing profiles
    /// should be moved into the newly chosen folder. Kept as a callback so the VM stays
    /// window-agnostic (mirrors <see cref="RequestClose"/>).</summary>
    public Func<string, Task<bool>>? ConfirmMoveProfiles { get; set; }

    /// <summary>Set by the host to refresh the profile list after a successful relocation (the service
    /// now serves a different directory). Kept as a callback so the VM stays shell-agnostic.</summary>
    public Func<Task>? ProfilesRelocated { get; set; }

    /// <summary>Changes the profiles storage folder. Applied immediately (and transactionally) on the
    /// service via a dedicated IPC call — independent of the Save button — because it moves files and
    /// reloads the catalog. The generic Save just re-persists the resulting path.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeProfilesDirectory))]
    private async Task ChangeProfilesDirectory()
    {
        ErrorMessage = null;
        StatusMessage = null;

        // The whole body is guarded: even Path.GetFullPath can throw (corrupt settings can hand us
        // an empty ProfilesDirectory), and an escape from the command boundary would take down the
        // UI thread unlogged.
        try
        {
            string? picked = await _folderPicker.PickFolderAsync(
                "Choose the profiles storage folder", ProfilesDirectory.Value);
            if (picked is null)
                return;
            if (IsSameFolder(picked, ProfilesDirectory.Value))
                return;   // same folder — nothing to do

            bool move = ConfirmMoveProfiles is not null
                && await ConfirmMoveProfiles(
                    $"Move the existing profiles into \"{picked}\"? Choose No to start fresh there and leave the current profiles where they are.");

            IsBusy = true;
            var result = await _gateway.RelocateProfilesAsync(picked, move);
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not change the profiles folder: {error.Message}";
                return;
            }
            result.TryGetValue(out RelocateProfilesResponse? outcome);
            ProfilesDirectory.Value = outcome!.Settings.ProfilesDirectory;
            if (outcome.SkippedFiles.Count > 0)
                // Never report a clean success over a collision: the new folder's pre-existing
                // (possibly stale) copies are the ones in use now.
                ErrorMessage =
                    $"Profiles folder changed, but {outcome.SkippedFiles.Count} profile file(s) stayed in the old folder " +
                    "because the new folder already had files with the same names — those pre-existing copies are the ones in use.";
            else
                StatusMessage = move
                    ? $"Profiles folder changed; {outcome.MovedCount} profile file(s) moved."
                    : "Profiles folder changed.";
            if (ProfilesRelocated is not null)
                await ProfilesRelocated();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Changing the profiles folder failed unexpectedly");
            ErrorMessage = $"Could not change the profiles folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChangeProfilesDirectory() => !IsBusy;

    /// <summary>Same-location check tolerant of bad current state: an empty/invalid current
    /// directory never matches (the relocation proceeds and the service validates).</summary>
    private static bool IsSameFolder(string picked, string current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return false;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Set by the host so a successful save can close the dialog. Kept as a callback so the
    /// VM stays window-agnostic (mirrors the folder-picker seam).</summary>
    public Action? RequestClose { get; set; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        // A load replaces the edited state wholesale; none of it is a user edit. Suppress covers the
        // whole body (including the early error returns) and Reset in the finally guarantees the window
        // opens clean even when the load failed part-way.
        using IDisposable suppressed = History.Suppress();
        try
        {
            var result = await _gateway.GetSettingsAsync();
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not load settings: {error.Message}";
                return;
            }
            result.TryGetValue(out GlobalSettings? settings);
            Theme.Value = settings!.ThemeMode;
            StartupMode.Value = settings.ServiceStartupMode;
            ScratchDirectory.Value = settings.ScratchDirectory;
            ProfilesDirectory.Value = settings.ProfilesDirectory;

            ScanThreadingSettings st = settings.ScanThreading;
            ApplyBudget(MaxScanThreads, st.MaxScanThreads, ScanAutoDefault);
            ApplyBudget(MaxHashThreads, st.MaxHashThreads, HashAutoDefault);
            ApplyBudget(PerDriveDefault, st.PerDriveDefault, PerDriveAutoDefault);

            DriveOverrides.DriveTypeOverrides.Clear();
            foreach (KeyValuePair<DriveClass, ThreadBudget> e in st.DriveTypeOverrides)
            {
                (bool auto, int value) = FromBudget(e.Value, PerDriveAutoDefault);
                DriveOverrides.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = e.Key, Auto = auto, Value = value });
            }

            DriveOverrides.SpecificDriveOverrides.Clear();
            foreach (KeyValuePair<string, ThreadBudget> e in st.SpecificDriveOverrides)
            {
                (bool auto, int value) = FromBudget(e.Value, PerDriveAutoDefault);
                SpecificDriveOverrideRowViewModel row = DriveOverrides.NewSpecificRow();
                (row.VolumeKey, row.Auto, row.Value) = (e.Key, auto, value);
                DriveOverrides.SpecificDriveOverrides.Add(row);
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
            History.Reset();     // the loaded state is the baseline: nothing to undo, nothing unsaved
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
            foreach (DriveTypeOverrideRowViewModel row in DriveOverrides.DriveTypeOverrides)
            {
                if (!byType.TryAdd(row.Class, ToBudget(row.Auto, row.Value)))
                {
                    ErrorMessage = $"Duplicate drive-type override for {row.Class}.";
                    return;
                }
            }

            Dictionary<string, ThreadBudget> specific = [];
            foreach (SpecificDriveOverrideRowViewModel row in DriveOverrides.SpecificDriveOverrides)
            {
                // Same normalizer the engine looks up with, so "C:\" and "c:" are one key and both match
                // a real volume.
                string key = VolumeKeys.Normalize(row.VolumeKey);
                if (key.Length == 0)
                    continue;
                if (!specific.TryAdd(key, ToBudget(row.Auto, row.Value)))
                {
                    ErrorMessage = $"Duplicate volume key \"{key}\".";
                    return;
                }
            }

            string scratch = ScratchDirectory.Value?.Trim() ?? "";
            if (scratch.Length == 0)
            {
                ErrorMessage = "The scratch directory cannot be empty.";
                return;
            }
            if (!Path.IsPathFullyQualified(scratch))
            {
                ErrorMessage = "The scratch directory must be an absolute path.";
                return;
            }

            GlobalSettings settings = new()
            {
                ThemeMode = Theme.Value,
                ServiceStartupMode = StartupMode.Value,
                ScratchDirectory = scratch,
                // The profiles directory is changed transactionally via ChangeProfilesDirectory; carry
                // the current value through so a generic Save never resets it to the default.
                ProfilesDirectory = ProfilesDirectory.Value,
                ScanThreading = new ScanThreadingSettings
                {
                    MaxScanThreads = ToBudget(MaxScanThreads.Auto, MaxScanThreads.Value),
                    MaxHashThreads = ToBudget(MaxHashThreads.Auto, MaxHashThreads.Value),
                    PerDriveDefault = ToBudget(PerDriveDefault.Auto, PerDriveDefault.Value),
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
            ThemeApplier.Apply(Theme.Value);      // apply the selected theme app-wide on save
            // Clears the unsaved-changes flag without dropping the history, so the user can still step
            // back through what they just saved.
            History.MarkSaved();
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

    private static void ApplyBudget(AutoNumberSettingViewModel setting, ThreadBudget budget, int autoDefault) =>
        (setting.Auto, setting.Value) = FromBudget(budget, autoDefault);

    private static (bool Auto, int Value) FromBudget(ThreadBudget budget, int autoDefault) =>
        budget.Value is int v ? (false, v) : (true, autoDefault);

    private static ThreadBudget ToBudget(bool auto, int value) =>
        auto ? ThreadBudget.Auto : ThreadBudget.Explicit(Math.Max(1, value));
}
