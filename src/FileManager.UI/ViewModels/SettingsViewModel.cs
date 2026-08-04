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
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Edits both halves of the settings surface: the client-side <see cref="ClientSettings"/>
/// (theme, service executable path) and the machine-level <see cref="GlobalSettings"/> (service
/// startup and the scan/hash thread budgets, <see cref="ScanThreadingSettings"/>). Scan concurrency is
/// global-only now: profiles no longer override it.
///
/// The two halves load and save independently, and that is the point. <see cref="GlobalSettings"/>
/// belongs to the service and travels over IPC, so when the service is unreachable it can be neither
/// read nor written — the settings that need it are disabled, and Save leaves them alone. The client
/// half is a local file, so it always works, which is what makes
/// <see cref="ServiceExePath"/> able to fix an unreachable service.
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
    private readonly string _clientSettingsPath;

    /// <summary>Set when <see cref="LoadAsync"/> could not read the stored <see cref="GlobalSettings"/>,
    /// so that half of the editable state is still the constructor's defaults rather than anything the
    /// user has. Save skips the GlobalSettings write while it is set: building a complete
    /// <see cref="GlobalSettings"/> out of those defaults and persisting it would reset a relocated
    /// ProfilesDirectory (making the user's whole profile list vanish, since ProfileStore resolves that
    /// directory live) and drop every per-drive override. The client-side half is unaffected — it never
    /// came from the service and is saved regardless.</summary>
    private bool _loadFailed;

    /// <summary>The client settings as last read or written, so <see cref="SaveAsync"/> can tell a real
    /// edit from an untouched value and leave the file alone when nothing changed.</summary>
    private ClientSettings _storedClient = ClientSettings.Default;

    public SettingsViewModel(IIpcGateway gateway, IFolderPicker folderPicker, ISystemDrives? drives = null,
        string? clientSettingsPath = null)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
        // Test seam, mirroring MainWindowViewModel: tests pass an isolated path so opening the settings
        // window never reads or writes the developer's real %LOCALAPPDATA% file.
        _clientSettingsPath = clientSettingsPath ?? UiPaths.ClientSettingsFilePath;

        Theme = Setting.Choice(
            "application.theme", "Theme",
            "The application theme. System follows the Windows light/dark preference; Light and Dark force a fixed theme.",
            [ThemeMode.System, ThemeMode.Light, ThemeMode.Dark],
            ["appearance", "colour", "color", "dark mode", "light mode"]);

        ServiceExePath = Setting.Text(
            "application.serviceExePath", "Service Executable Path",
            "Full path to FileManager.Service.exe. Leave blank to use the copy installed beside this " +
            "app. Set it only if the status bar says the service executable could not be found — this " +
            "setting is saved on this computer and takes effect without restarting the app.",
            ["service", "executable", "exe", "path", "launch", "start", "missing", "not found",
             "location", "background", "unreachable", "disconnected"],
            placeholder: "Leave blank to use the default location",
            actionButtonText: "Browse…", actionCommand: BrowseServiceExePathCommand);

        StartupMode = Setting.Choice(
            "startup.serviceMode", "Startup Mode",
            "When the background service runs. Run on Startup launches it at Windows login. Start on Program Open starts it when this app opens and leaves it running. Start and Stop with Program starts it on open and stops it when the app closes (warning first if jobs are running).",
            [ServiceStartupMode.RunOnStartup, ServiceStartupMode.StartOnProgramOpen, ServiceStartupMode.StartAndStopWithProgram],
            ["service", "background", "autostart", "auto start", "login", "boot", "windows"]);

        MaxScanThreads = Setting.AutoNumber(
            "performance.maxScanThreads", "Max Scan Threads",
            $"Caps the total number of directory-enumeration threads across all profiles. Auto uses cores × 4 ({ScanAutoDefault} on this machine, capped at 64).",
            ["performance", "concurrency", "parallel", "cpu", "workers", "enumeration", "threads"]);

        PerDriveDefault = Setting.AutoNumber(
            "performance.perDriveDefault", "Per-Drive Default",
            $"Caps the enumeration threads used on any one drive. Auto uses cores × 2 ({PerDriveAutoDefault} on this machine).",
            ["performance", "concurrency", "parallel", "cpu", "workers", "disk", "volume", "threads"]);

        MaxHashThreads = Setting.AutoNumber(
            "performance.maxHashThreads", "Max Hash Threads",
            $"Caps parallel file hashing during a dry run. Auto uses cores − 1 ({HashAutoDefault} on this machine).",
            ["performance", "concurrency", "parallel", "cpu", "workers", "hash", "checksum", "verify", "threads"]);

        MaxScanDepth = Setting.AutoNumber(
            "performance.maxScanDepth", "Max Scan Depth",
            "A safety stop for directory trees that loop back on themselves — most often a symlink on a "
            + $"network share, which Windows cannot flag as a link. Auto uses {GlobalSettings.DefaultMaxScanDepth} "
            + "levels below each scan root; nothing below the limit is scanned. Raise it only for a "
            + "genuinely deeper tree, and use a profile's own depth filter to limit a scan on purpose.",
            ["performance", "depth", "recursion", "levels", "nested", "loop", "cycle", "symlink",
             "junction", "reparse", "network", "share", "safety", "limit"]);

        DriveOverrides = new DriveOverridesSettingViewModel(
            "performance.driveOverrides", "Per-Drive Overrides",
            "Override the per-drive budget for a whole drive type, or for a specific volume key (highest precedence). Precedence: specific drive → drive type → per-drive default.",
            PerDriveAutoDefault,
            drives ?? new SystemDrives(),
            ["performance", "advanced", "drive", "disk", "volume", "network", "removable", "optical", "override"]);

        ScratchDirectory = Setting.Text(
            "storage.scratchDirectory", "Dry-Run Scratch Directory",
            "Where the service spills a large dry run's findings to disk before streaming them to this window. Small runs stay in memory and never touch it. Leftover snapshots are purged on service startup.",
            ["storage", "folder", "directory", "path", "temp", "spill", "dry run", "location"],
            actionButtonText: "Browse…", actionCommand: BrowseScratchDirectoryCommand);

        ReleaseMemoryAfterLargeOperations = Setting.Bool(
            "performance.releaseMemory", "Optimize Memory",
            "After a large dry run settles, ask the service to compact and hand its peak memory back " +
            "to Windows. Without this the service keeps showing that peak in Task Manager until it " +
            "restarts. The collection is brief and only ever runs while the service is idle. Turn it " +
            "off if you run dry runs back to back and would rather keep the warm heap.",
            ["performance", "memory", "ram", "footprint", "gc", "garbage", "collect", "compact",
             "release", "trim", "leak", "usage", "task manager"],
            checkBoxLabel: "Release memory when idle");

        ProfilesDirectory = Setting.Text(
            "storage.profilesDirectory", "Profiles Storage",
            "Where profile files are stored. Change… applies immediately: it asks whether to move the existing profiles into the new folder (defaulting to no), then switches the service to it.",
            ["storage", "folder", "directory", "path", "profiles", "location", "move"],
            readOnly: true,
            actionButtonText: "Change…", actionCommand: ChangeProfilesDirectoryCommand,
            // Change… relocates files on disk immediately, so there is nothing to undo and nothing left
            // pending for Save. Tracking it would also make the window read as dirty right after a
            // relocation that already succeeded.
            undoable: false);

        ReleaseMemoryAfterLargeOperations.Value = GlobalSettings.Default.ReleaseMemoryAfterLargeOperations;
        ScratchDirectory.Value = GlobalSettings.DefaultScratchDirectory;
        ProfilesDirectory.Value = GlobalSettings.DefaultProfilesDirectory;
        MaxScanThreads.Value = ScanAutoDefault;
        PerDriveDefault.Value = PerDriveAutoDefault;
        MaxHashThreads.Value = HashAutoDefault;
        MaxScanDepth.Value = GlobalSettings.DefaultMaxScanDepth;

        BuildCatalog();
        BuildNavigation();
        TrackForUndo();      // last: the recorder caches the values the catalog was built with

        // Live verdict on the executable path, so the user sees what a value resolves to while typing
        // rather than discovering after a save that it silently fell back.
        ServiceExePath.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextSettingViewModel.Value))
                _ = RefreshServiceExeNoticeAsync();
        };
    }

    // Shadow "explicit" defaults shown when a budget's Auto is unchecked, matching the engine's auto
    // formulas so the starting number is the value auto would have chosen.
    private static int ScanAutoDefault => Math.Min(Environment.ProcessorCount * 4, 64);
    private static int HashAutoDefault => Math.Max(1, Environment.ProcessorCount - 1);
    private static int PerDriveAutoDefault => Environment.ProcessorCount * 2;

    // ============================ The settings themselves ============================
    // Held as strongly-typed handles so the load/save mapping and the tests stay type-safe; the view
    // never touches these directly, it renders Categories.

    public ChoiceSettingViewModel<ThemeMode> Theme { get; }
    public TextSettingViewModel ServiceExePath { get; }
    public ChoiceSettingViewModel<ServiceStartupMode> StartupMode { get; }
    public AutoNumberSettingViewModel MaxScanThreads { get; }
    public AutoNumberSettingViewModel PerDriveDefault { get; }
    public AutoNumberSettingViewModel MaxHashThreads { get; }
    public AutoNumberSettingViewModel MaxScanDepth { get; }
    public DriveOverridesSettingViewModel DriveOverrides { get; }
    public BoolSettingViewModel ReleaseMemoryAfterLargeOperations { get; }
    public TextSettingViewModel ScratchDirectory { get; }
    public TextSettingViewModel ProfilesDirectory { get; }

    /// <summary>The document: every category in display order. Categories and their settings are fixed
    /// once built; searching toggles <c>IsVisible</c> rather than rebuilding, so nothing loses focus
    /// mid-edit.</summary>
    public ObservableCollection<SettingsCategoryViewModel> Categories { get; } = [];

    /// <summary>The left-hand navigation tree, mirroring <see cref="Categories"/>.</summary>
    public ObservableCollection<SettingsNavNodeViewModel> NavNodes { get; } = [];

    /// <summary>Declares the categories and the settings in each. This is the only place a new setting
    /// has to be registered for it to appear in the document, the navigation tree, and the search.
    ///
    /// Categories are grouped by OWNERSHIP, and the order matters: the client-side ones come first so
    /// that when the service is unreachable the window reads as "these you can still change, everything
    /// below needs the service" — with the fix for an unreachable service sitting in the part that
    /// still works. <see cref="SettingsCategoryViewModel.RequiresService"/> is the switch.</summary>
    private void BuildCatalog()
    {
        SettingsCategoryViewModel performance = new(
            "performance", "Scan Performance", "Thread budgets and scan limits shared across all profiles.")
        { RequiresService = true };

        Categories.Add(new SettingsCategoryViewModel(
            "application", "Application")
            .With(Theme, ServiceExePath));
        Categories.Add(new SettingsCategoryViewModel("startup", "Service Startup") { RequiresService = true }
            .With(StartupMode));
        Categories.Add(performance.With(
            MaxScanThreads, PerDriveDefault, MaxHashThreads, MaxScanDepth, ReleaseMemoryAfterLargeOperations));
        Categories.Add(new SettingsCategoryViewModel("performance.advanced", "Per-Drive Overrides", parent: performance)
            { RequiresService = true }
            .With(DriveOverrides));
        Categories.Add(new SettingsCategoryViewModel("storage", "Storage") { RequiresService = true }
            .With(ScratchDirectory, ProfilesDirectory));
    }

    /// <summary>Greys out (or restores) every setting that lives in the service-owned
    /// <see cref="GlobalSettings"/>. Called after a load so the window can never present a
    /// service-backed value that was not actually loaded, nor accept an edit that Save would drop.</summary>
    private void SetServiceSettingsEnabled(bool enabled)
    {
        foreach (SettingsCategoryViewModel category in Categories)
        {
            foreach (SettingItemViewModel item in category.Items)
            {
                if (item.RequiresService)
                    item.IsEnabled = enabled;
            }
        }
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
            DiscardChangesCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>True when there are edits the Save button has not persisted.</summary>
    public bool IsDirty => History.IsDirty;

    /// <summary>What the close button says. Naming the consequence at the moment of the click is the
    /// whole point: closing discards pending edits, and should say so. Distinct from the Discard
    /// button beside it, which reverts the edits and leaves the window open.</summary>
    public string CloseButtonText => IsDirty ? "Close without saving" : "Close";

    /// <summary>Throws away pending edits by reloading the persisted state, rather than by walking the
    /// undo stack back to the saved marker. Reloading is unconditionally correct: after a save followed
    /// by an undo and a fresh edit, the marker has left with the discarded redo branch and the saved
    /// position is no longer reachable through the stacks at all. It also resets the history, which is
    /// what "discard" should mean — there is nothing left to step back through.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscardChanges))]
    private async Task DiscardChanges()
    {
        await LoadAsync();
        StatusMessage = "Changes discarded.";
    }

    private bool CanDiscardChanges() => IsDirty && !IsBusy;

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
        if (value.Setting is { } setting)
            SelectSetting(setting);
        else if (value.Category is { } category)
            SelectCategory(category);
    }

    /// <summary>The setting the tree last navigated to, or null before any navigation, or once a category
    /// (rather than a setting) was navigated to instead.</summary>
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

    /// <summary>Selecting a category shows its own header rather than jumping into one of its settings —
    /// so no setting card stays highlighted while the header is the navigation target.</summary>
    private void SelectCategory(SettingsCategoryViewModel category)
    {
        History.BreakMerge();
        if (SelectedSetting is not null)
        {
            SelectedSetting.IsSelected = false;
            SelectedSetting = null;
        }
        ScrollToCategoryRequested?.Invoke(this, category);
    }

    /// <summary>Raised when a setting should be brought into view. Handled by the window, because
    /// scrolling is a view concern and the VM stays window-agnostic (mirrors <see cref="RequestClose"/>).</summary>
    public event EventHandler<SettingItemViewModel>? ScrollToRequested;

    /// <summary>Raised when a category's own header should be brought into view (mirrors
    /// <see cref="ScrollToRequested"/>).</summary>
    public event EventHandler<SettingsCategoryViewModel>? ScrollToCategoryRequested;

    // ============================ Shell state ============================

    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeProfilesDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardChangesCommand))]
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
            // FIRST, and deliberately outside everything below: these are local, and reading them
            // before the IPC call is what stops a failed load from presenting a blank service
            // executable path — which a subsequent Save would then persist, erasing the working
            // override that is the user's way out of an unreachable service.
            _storedClient = ClientSettingsStore.Read(_clientSettingsPath);
            Theme.Value = _storedClient.ThemeMode;
            ServiceExePath.Value = _storedClient.ServiceExecutablePath ?? "";
            // Await it here (unlike the fire-and-forget on edit) so a window opened against a stored
            // path that has since gone missing shows the warning on its first paint.
            await RefreshServiceExeNoticeAsync();

            var result = await _gateway.GetSettingsAsync();
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = error.Code == "SERVICE_UNAVAILABLE"
                    ? $"Could not load the service's settings: {error.Message} The settings above can still be changed."
                    : $"Could not load the service's settings: {error.Message}";
                _loadFailed = true;
                // Nothing below was loaded, so nothing below may be edited: an edit the user made here
                // would be silently dropped by Save, and the value shown would be a default they never
                // chose. The application settings stay live.
                SetServiceSettingsEnabled(false);
                return;
            }
            result.TryGetValue(out GlobalSettings? settings);
            StartupMode.Value = settings!.ServiceStartupMode;
            ScratchDirectory.Value = settings.ScratchDirectory;
            ReleaseMemoryAfterLargeOperations.Value = settings.ReleaseMemoryAfterLargeOperations;
            ProfilesDirectory.Value = settings.ProfilesDirectory;

            // Auto here means "the shipped ceiling", so a stored value equal to it reads back as Auto —
            // the same absent-means-default round-trip GlobalSettings.MaxScanDepth itself performs.
            (MaxScanDepth.Auto, MaxScanDepth.Value) =
                (settings.MaxScanDepth == GlobalSettings.DefaultMaxScanDepth, settings.MaxScanDepth);

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

            // Last: the whole mapping above succeeded, so the editable state is now the stored state
            // and Save has something real to write.
            _loadFailed = false;
            SetServiceSettingsEnabled(true);
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes an error banner, not an unobserved fault.
            Serilog.Log.Error(ex, "Loading global settings failed unexpectedly");
            ErrorMessage = $"Could not load the service's settings: {ex.Message}";
            _loadFailed = true;
            SetServiceSettingsEnabled(false);
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
            // The client half first, and before the _loadFailed gate below. These values are local and
            // were really loaded, so nothing about an unreachable service makes them unsafe to write —
            // and refusing would make the service executable path useless at the only moment it
            // matters. The gate still protects every GlobalSettings value, which is what it is for.
            // Captured before the write, so the switchover below can tell an actual change from a save
            // that merely touched other settings — this must only ever trigger on a real edit.
            string? previousExePath = _storedClient.ServiceExecutablePath;
            string? exePath = SaveClientSettings();
            bool exePathChanged = !PathsEqual(previousExePath, exePath);
            ThemeApplier.Apply(Theme.Value);              // applies whether or not the service is up

            // Re-check the saved path so the notice under the box reflects what was actually stored.
            // The window stays open after a save regardless, which is what makes a warning here
            // readable — it used to be dismissed along with the dialog the instant it was set.
            ServiceExeResolution resolution = await Task.Run(() => ServiceLauncher.Resolve(exePath));
            ApplyServiceExeNotice(resolution);

            // A load that failed left the service's settings at their constructor defaults, so writing
            // them here would overwrite the stored file with defaults rather than with the user's
            // settings. Skip that half instead: the service is a separate process and can simply be
            // mid-restart. Marking saved is honest here precisely because those settings are disabled
            // while the load has failed — the user cannot have pending edits to them.
            if (_loadFailed)
            {
                StatusMessage = "Application settings saved.";
                ErrorMessage = "The service's own settings could not be loaded and were left unchanged.";
                History.MarkSaved();
                // Nothing is connected, so there is nothing to offer to shut down — but the cached
                // connection state still has to be cleared so the next attempt starts the new path
                // instead of serving out a cooldown the old one earned.
                if (exePathChanged)
                    await _gateway.ResetConnectionAsync();
                return;
            }

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
                ServiceStartupMode = StartupMode.Value,
                ScratchDirectory = scratch,
                ReleaseMemoryAfterLargeOperations = ReleaseMemoryAfterLargeOperations.Value,
                // The profiles directory is changed transactionally via ChangeProfilesDirectory; carry
                // the current value through so a generic Save never resets it to the default.
                ProfilesDirectory = ProfilesDirectory.Value,
                MaxScanDepth = MaxScanDepth.Auto ? GlobalSettings.DefaultMaxScanDepth : MaxScanDepth.Value,
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
            // Clears the unsaved-changes flag without dropping the history, so the user can still step
            // back through what they just saved.
            History.MarkSaved();

            // LAST, and deliberately after the GlobalSettings write: switching executables can stop the
            // service, which would make that write fail if it were still pending.
            if (exePathChanged)
                await SwitchServiceExecutableAsync(resolution);
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

    /// <summary>Persists the client-side settings and returns the path stored for the service
    /// executable (null when the setting is blank).
    ///
    /// Nothing here can refuse a save. An unusable path is no longer fatal — resolution falls back to
    /// the next candidate — and refusing would be the wrong shape anyway: the state worth warning
    /// about ("your path is being ignored, here is what is being used instead") could then never exist,
    /// and a path that was valid when saved can go missing later regardless. <see cref="ServiceExePath"/>'s
    /// notice does the telling, and <see cref="SaveAsync"/> keeps the window open when it is not Info.
    ///
    /// Writes only on a real change, so a save that never touched these never touches the file — which
    /// also keeps read-modify-write honest against the sidebar, the other writer of this file.</summary>
    private string? SaveClientSettings()
    {
        string typed = ServiceExePath.Value?.Trim() ?? "";

        // A hand-typed folder is the likeliest near-miss (Browse can only return a file), and it is
        // unambiguous when the executable is sitting in it — so complete it rather than scold.
        if (typed.Length > 0
            && Directory.Exists(typed)
            && File.Exists(Path.Combine(typed, ServiceLauncher.ServiceExeName)))
        {
            typed = Path.Combine(typed, ServiceLauncher.ServiceExeName);
        }

        // Blank means "no override", stored as absent rather than as an empty string so the file says
        // what it means and the launcher's IsNullOrWhiteSpace check reads naturally.
        string? exePath = typed.Length == 0 ? null : typed;

        // Normalisation (trimming, folder completion) is not a user edit, so it must not land on the
        // undo stack as a step the user then has to undo twice.
        using (History.Suppress())
            ServiceExePath.Value = exePath ?? "";

        ClientSettings updated = _storedClient with
        {
            ServiceExecutablePath = exePath,
            ThemeMode = Theme.Value,
        };
        if (updated == _storedClient)
            return exePath;

        // Update, not Read-then-Write: the sidebar writes its own half of this file on its own
        // schedule, so the copy read at load time may already be stale, and doing the read and the
        // write as separate steps would let a sidebar save land between them and be lost.
        _storedClient = ClientSettingsStore.Update(_clientSettingsPath, stored => stored with
        {
            ServiceExecutablePath = exePath,
            ThemeMode = Theme.Value,
        });
        return exePath;
    }

    // ============================ Switching to a new service executable ============================

    /// <summary>Set by the host to ask (modal Yes/No) whether to stop the service that is already
    /// running, when the user points the executable path somewhere else. Kept as a callback so the VM
    /// stays window-agnostic (mirrors <see cref="ConfirmMoveProfiles"/>).</summary>
    public Func<string, Task<bool>>? ConfirmStopPreviousService { get; set; }

    /// <summary>How long to wait for the newly configured executable to take over. Internal purely as a
    /// test seam (mirrors <c>JobProgressPublisher.Interval</c>) — none of these are user-configurable.</summary>
    internal int SwitchAttempts { get; init; } = 20;
    internal TimeSpan SwitchPollDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How many of those rounds may spawn a process. Bounded well below
    /// <see cref="SwitchAttempts"/> and NOT equal to 1: the stopped service does not release its
    /// single-instance mutex the instant it acknowledges the shutdown, so the first launch can lose that
    /// race and exit immediately, and one attempt would report a working executable as broken. Every
    /// later round is a plain reconnect. Without this bound an executable that starts but never serves —
    /// the exact case the path notice warns about — is launched once per round, each burning the
    /// launcher's full ~5 s retry budget: twenty stray processes and a Save that hangs for minutes.</summary>
    internal int SwitchStartAttempts { get; init; } = 3;

    /// <summary>Hard ceiling on the whole wait, independent of the round arithmetic above, so a
    /// pathologically slow connect attempt cannot leave the window busy indefinitely.</summary>
    internal TimeSpan SwitchTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Makes the running service match the executable the user just chose. Outcomes are
    /// reported through <see cref="StatusMessage"/> / <see cref="ErrorMessage"/>, which the window
    /// keeps showing — it no longer closes on save, so there is nothing to gate.
    ///
    /// <para>Whatever is serving right now was resolved by whoever started it — which may not have
    /// been this client at all (autostart, or a previous session), so the only truthful source for
    /// "which executable is actually running" is the service's own report in the status snapshot.</para></summary>
    private async Task SwitchServiceExecutableAsync(ServiceExeResolution resolution)
    {
        if (resolution.Chosen is not { } desired)
            return;      // nothing usable to switch TO; the notice already says so

        var status = await _gateway.GetStatusAsync();
        if (!status.TryGetValue(out EngineStatusSnapshot? running))
        {
            // Not connected: nothing to shut down, and the reset lets the next attempt start the newly
            // configured executable straight away.
            await _gateway.ResetConnectionAsync();
            return;
        }

        if (running.ExecutablePath is { } current && PathsEqual(current, desired.Path))
            return;      // already the one the user wants — starting a second would duplicate it

        string where = running.ExecutablePath is { Length: > 0 } path ? $"\"{path}\"" : "another location";
        string jobs = running.JobsInFlight > 0
            ? $" It currently has {running.JobsInFlight} job(s) running, which stopping it would interrupt."
            : "";
        bool stop = ConfirmStopPreviousService is not null
            && await ConfirmStopPreviousService(
                $"A service is already running from {where}.{jobs}\n\n" +
                $"Stop it and start the service from \"{desired.Path}\" instead? " +
                "Choose Keep running to leave it alone — this app will keep using it until it stops.");

        if (!stop)
        {
            // Deliberately does NOT start the new one. Only one service can hold the pipe (and the
            // single-instance mutex), so a second would exit immediately anyway — and the user just
            // said they did not want another one.
            StatusMessage = "Saved. The service already running was left alone, and this app keeps " +
                            "using it until it stops.";
            return;
        }

        var stopped = await _gateway.ShutdownServiceAsync();
        if (stopped.TryGetError(out IpcError? stopError))
        {
            Serilog.Log.Warning("Stopping the previous service failed: {Code} {Message}",
                stopError.Code, stopError.Message);
            ErrorMessage = $"Could not stop the service that is already running: {stopError.Message}. " +
                           "The new executable path is saved, but it will not be used until that " +
                           "service stops.";
            return;
        }

        if (await WaitForServiceAtAsync(desired.Path))
        {
            StatusMessage = $"Saved. Now running the service from \"{desired.Path}\".";
            return;
        }

        ErrorMessage = $"The previous service was stopped, but the service could not be started from " +
                       $"\"{desired.Path}\" — check that it is really the service executable.";
    }

    /// <summary>Reconnects until the service reports it is running from <paramref name="desired"/>.
    /// <para>Verifying the path rather than merely connecting is the point: the old process does not
    /// exit the instant it acknowledges a shutdown, so a plain connect can succeed against the very
    /// service that is on its way out and report a switch that never happened.</para>
    /// <para>Only the first <see cref="SwitchStartAttempts"/> rounds are allowed to spawn a process —
    /// enough to retry a launch that lost the race to the old process's single-instance mutex, few
    /// enough that an executable which never serves cannot be launched once per round. The remaining
    /// rounds reset with <c>allowStart: false</c>, which arms the gateway's cooldown so they are plain
    /// reconnects rather than launches.</para></summary>
    private async Task<bool> WaitForServiceAtAsync(string desired)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int attempt = 0; attempt < SwitchAttempts; attempt++)
        {
            await _gateway.ResetConnectionAsync(allowStart: attempt < SwitchStartAttempts);
            var status = await _gateway.GetStatusAsync();
            if (status.TryGetValue(out EngineStatusSnapshot? snapshot)
                && snapshot.ExecutablePath is { } path
                && PathsEqual(path, desired))
            {
                return true;
            }
            if (elapsed.Elapsed >= SwitchTimeout)
            {
                Serilog.Log.Warning(
                    "Gave up waiting for the service at {Path} after {Elapsed} and {Attempts} attempt(s)",
                    desired, elapsed.Elapsed, attempt + 1);
                return false;
            }
            await Task.Delay(SwitchPollDelay);
        }
        return false;
    }

    /// <summary>File-path equality tolerant of casing, trailing separators and relative spelling. Blank
    /// equals blank, so "was not set" and "still not set" is not read as a change.</summary>
    private static bool PathsEqual(string? left, string? right)
    {
        bool leftBlank = string.IsNullOrWhiteSpace(left);
        bool rightBlank = string.IsNullOrWhiteSpace(right);
        if (leftBlank || rightBlank)
            return leftBlank && rightBlank;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left!.Trim())),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right!.Trim())),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed path cannot be normalised; fall back to comparing what was typed rather than
            // letting a bad string abort a save.
            return string.Equals(left!.Trim(), right!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ============================ Service executable diagnostics ============================

    /// <summary>Recomputes <see cref="ServiceExePath"/>'s notice for the value currently in the box.
    /// Fire-and-forget from the property change hook, so the user sees the verdict while typing rather
    /// than only after saving.
    /// <para>The resolution touches the filesystem, which is why it runs off the UI thread: probing a
    /// half-typed UNC path can block for seconds on an SMB timeout, and doing that inline would freeze
    /// the settings window one keystroke at a time. The staleness check drops results the user has
    /// already typed past, so out-of-order completions cannot leave a wrong verdict on screen.</para></summary>
    private async Task RefreshServiceExeNoticeAsync()
    {
        string configured = ServiceExePath.Value?.Trim() ?? "";
        ServiceExeResolution resolution = await Task.Run(() => ServiceLauncher.Resolve(configured));
        if (!string.Equals(ServiceExePath.Value?.Trim() ?? "", configured, StringComparison.Ordinal))
            return;      // superseded by a later keystroke
        ApplyServiceExeNotice(resolution);
    }

    private void ApplyServiceExeNotice(ServiceExeResolution resolution)
    {
        (string text, SettingNoticeSeverity severity) = DescribeServiceExe(resolution);
        ServiceExePath.SetNotice(text, severity);
    }

    /// <summary>Turns a resolution into the line shown under the box. Pure, so every case is testable
    /// without a filesystem.</summary>
    internal static (string Text, SettingNoticeSeverity Severity) DescribeServiceExe(ServiceExeResolution resolution)
    {
        ServiceExeCandidate? configured = resolution.Configured;
        ServiceExeCandidate? chosen = resolution.Chosen;

        if (configured is null)
        {
            // Blank: say which copy that actually means, so "leave blank" is not a leap of faith.
            return chosen is null
                ? ($"No service executable could be found. {CheckedList(resolution)} The service " +
                   "cannot be started until one of these exists or a valid path is set here.",
                   SettingNoticeSeverity.Error)
                : ($"Using {Describe(chosen.Source)}: \"{chosen.Path}\".", SettingNoticeSeverity.Info);
        }

        if (!configured.IsUsable)
        {
            // The case that prompted all of this: everything keeps working, so without saying it out
            // loud nothing would ever tell the user their path is being ignored.
            return chosen is null
                ? ($"No file at this path, and no service executable could be found anywhere else. " +
                   $"{CheckedList(resolution)} The service cannot be started.",
                   SettingNoticeSeverity.Error)
                : ($"No file at this path — the service will start from {Describe(chosen.Source)} " +
                   $"instead: \"{chosen.Path}\". Clear this box to use that on purpose.",
                   SettingNoticeSeverity.Warning);
        }

        if (!string.Equals(Path.GetFileName(configured.Path), ServiceLauncher.ServiceExeName,
                StringComparison.OrdinalIgnoreCase))
        {
            // A renamed build is legitimate, so this is not an error — but pointing at some unrelated
            // program is the failure that looks like nothing is wrong: it launches fine and simply
            // never answers, which reads as "the service won't start" with no clue why.
            return ($"\"{Path.GetFileName(configured.Path)}\" is not called {ServiceLauncher.ServiceExeName}. " +
                    "If it is not really the service it will start and then never connect, and the app " +
                    "will keep reporting the service as unavailable.",
                    SettingNoticeSeverity.Warning);
        }

        return ($"Found. The service will start from \"{configured.Path}\".", SettingNoticeSeverity.Info);
    }

    private static string CheckedList(ServiceExeResolution resolution)
    {
        List<string> parts = [];
        foreach (ServiceExeCandidate candidate in resolution.Candidates)
            parts.Add($"\"{candidate.Path}\"");
        return parts.Count == 0 ? "" : $"Checked {string.Join(", ", parts)}.";
    }

    private static string Describe(ServiceExeSource source) => source switch
    {
        ServiceExeSource.Setting => "the path set here",
        ServiceExeSource.Environment => $"the {ServiceLauncher.ServiceExeOverrideVariable} environment variable",
        _ => "the copy installed beside this app",
    };

    [RelayCommand]
    private async Task BrowseServiceExePath()
    {
        string? picked = await _folderPicker.PickFileAsync(
            $"Choose {ServiceLauncher.ServiceExeName}", "Programs", ["*.exe"], ServiceExePath.Value);
        if (picked is not null)
            ServiceExePath.Value = picked;
    }

    private static void ApplyBudget(AutoNumberSettingViewModel setting, ThreadBudget budget, int autoDefault) =>
        (setting.Auto, setting.Value) = FromBudget(budget, autoDefault);

    private static (bool Auto, int Value) FromBudget(ThreadBudget budget, int autoDefault) =>
        budget.Value is int v ? (false, v) : (true, autoDefault);

    private static ThreadBudget ToBudget(bool auto, int value) =>
        auto ? ThreadBudget.Auto : ThreadBudget.Explicit(Math.Max(1, value));
}
