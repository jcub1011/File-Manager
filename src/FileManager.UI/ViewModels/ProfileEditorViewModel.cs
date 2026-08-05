using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using FileManager.UI.Undo;
using FileManager.UI.ViewModels.Editor;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Maps the immutable Profile record to a mutable draft and back. Fields the editor
/// does not own (Transformers, Triggers of an existing profile, Id, SchemaVersion) round-trip
/// through the stashed original untouched.
///
/// Edits are reversible: the draft declares its undoable properties (<see cref="UndoableProperties"/>)
/// and its row collections (<see cref="TrackNested"/>), and <see cref="UndoHistory"/> records them from
/// the change notifications they already raise. <see cref="IsDirty"/> falls out of the same history, so
/// undoing back to the loaded state genuinely clears it.</summary>
public sealed partial class ProfileEditorViewModel : ViewModelBase, IUndoTrackable
{
    /// <summary>Spec §5.1: the schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 2;

    private readonly IIpcGateway _gateway;
    private readonly IFolderPicker _folderPicker;
    private Profile? _original;
    private bool _loading;

    /// <summary>The user's AdditiveArchive scan preference, remembered while Mirror forces the flag on
    /// so switching back to AdditiveArchive restores their choice instead of leaving it stuck checked.</summary>
    private bool _scanDestinationPreference;

    /// <summary>Open only for the duration of a <see cref="SyncMode"/> assignment — see
    /// <see cref="OnPropertyChanging"/> for why the bracket cannot live inside the change hook.</summary>
    private IDisposable? _syncModeBatch;

    public ProfileEditorViewModel(IIpcGateway gateway, IFolderPicker folderPicker)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
        // Registers this draft's properties and, through TrackNested, the two row collections. From here
        // on the history is the only dirty-tracking mechanism: rows and collection edits need no
        // per-call-site bookkeeping.
        History.Track(this);
        History.PropertyChanged += OnHistoryChanged;
    }

    /// <summary>Invoked after a successful save so the shell can refresh the list.</summary>
    public Action<Guid>? Saved { get; set; }

    /// <summary>Invoked after the draft is discarded so the shell can reset dependent views
    /// (e.g. clear a dry-run preview generated from the now-discarded edits).</summary>
    public Action? Discarded { get; set; }

    // ----- enum options (static arrays: AOT-safe, no Enum.GetValues reflection) -----
    // Mirror is selectable and implemented: a run plans its orphan deletions through the same planner
    // the preview uses, then removes them to the Recycle Bin. Saving one requires acknowledging a
    // blocking warning (PROFILE_MIRROR_DELETES) because it deletes at the TARGET.
    public IReadOnlyList<SyncMode> SyncModeOptions { get; } = [SyncMode.AdditiveArchive, SyncMode.Mirror];
    public IReadOnlyList<TargetLayout> TargetLayoutOptions { get; } = [TargetLayout.PreserveStructure, TargetLayout.Flatten];
    public IReadOnlyList<ConflictResolution> ConflictResolutionOptions { get; } =
        [ConflictResolution.Skip, ConflictResolution.RenameSuffix, ConflictResolution.Overwrite, ConflictResolution.OverwriteIfNewer];
    public IReadOnlyList<OverwriteHandling> OverwriteHandlingOptions { get; } =
        [OverwriteHandling.StageOverwrites, OverwriteHandling.DirectOverwrite];
    public IReadOnlyList<MirrorDeletion> MirrorDeletionOptions { get; } =
        [MirrorDeletion.AfterCopy, MirrorDeletion.Proactive];
    public IReadOnlyList<VerificationMethod> VerificationOptions { get; } =
        [VerificationMethod.XxHash128, VerificationMethod.Sha256, VerificationMethod.None];  // SizeTimestamp is [reserved]
    public IReadOnlyList<LargeFileIdentity> LargeFileIdentityOptions { get; } =
        [LargeFileIdentity.FullHash, LargeFileIdentity.SampledHash,
         LargeFileIdentity.TimestampOrSampledHash, LargeFileIdentity.SizeAndTimestamp];
    public IReadOnlyList<OnSuccessAction> OnSuccessOptions { get; } =
        [OnSuccessAction.KeepSource, OnSuccessAction.MoveToArchive, OnSuccessAction.MoveToTrash, OnSuccessAction.PermanentDelete];
    public IReadOnlyList<MetadataOnConflict> MetadataOptions { get; } =
        [MetadataOnConflict.WarnAndContinue, MetadataOnConflict.FailJob];
    public IReadOnlyList<LogVerbosity> VerbosityOptions { get; } =
        [LogVerbosity.FailuresOnly, LogVerbosity.FailuresAndSkips, LogVerbosity.All];

    // ----- draft state -----
    [ObservableProperty] public partial bool HasProfile { get; set; }
    [ObservableProperty] public partial bool IsNew { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool Active { get; set; } = true;
    [ObservableProperty] public partial SyncMode SyncMode { get; set; } = SyncMode.AdditiveArchive;
    [ObservableProperty] public partial bool ScanDestination { get; set; }
    [ObservableProperty] public partial TargetLayout TargetLayout { get; set; } = TargetLayout.PreserveStructure;
    [ObservableProperty] public partial ConflictResolution ConflictResolution { get; set; } = ConflictResolution.Skip;
    [ObservableProperty] public partial OverwriteHandling OverwriteHandling { get; set; } = OverwriteHandling.StageOverwrites;
    [ObservableProperty] public partial MirrorDeletion MirrorDeletion { get; set; } = MirrorDeletion.AfterCopy;
    [ObservableProperty] public partial VerificationMethod VerificationMethod { get; set; } = VerificationMethod.XxHash128;
    [ObservableProperty] public partial LargeFileIdentity LargeFileIdentity { get; set; } = LargeFileIdentity.FullHash;
    [ObservableProperty] public partial string LargeFileIdentityThresholdText { get; set; } =
        PolicySettings.DefaultLargeFileIdentityThresholdBytes.ToString();
    [ObservableProperty] public partial OnSuccessAction OnSuccess { get; set; } = OnSuccessAction.KeepSource;
    [ObservableProperty] public partial string ArchiveFolder { get; set; } = "";
    [ObservableProperty] public partial MetadataOnConflict MetadataOnConflict { get; set; } = MetadataOnConflict.WarnAndContinue;
    [ObservableProperty] public partial LogVerbosity Verbosity { get; set; } = LogVerbosity.FailuresAndSkips;
    [ObservableProperty] public partial bool NotifyOnFailure { get; set; } = true;

    /// <summary>One glob per line.</summary>
    [ObservableProperty] public partial string IncludeGlobsText { get; set; } = "";
    [ObservableProperty] public partial string ExcludeGlobsText { get; set; } = "";
    [ObservableProperty] public partial string MinSizeText { get; set; } = "";
    [ObservableProperty] public partial string MaxSizeText { get; set; } = "";
    [ObservableProperty] public partial string MaxDepthText { get; set; } = "";

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<TargetRowViewModel> Targets { get; } = [];
    public ObservableCollection<ValidationIssueItem> Issues { get; } = [];

    [ObservableProperty] public partial bool CanAcknowledgeAndSave { get; set; }
    [ObservableProperty] public partial string? LocalError { get; set; }
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial bool ShowUnsavedWarning { get; set; }

    public bool ShowArchiveFolder => OnSuccess == OnSuccessAction.MoveToArchive;

    partial void OnOnSuccessChanged(OnSuccessAction value) => OnPropertyChanged(nameof(ShowArchiveFolder));

    /// <summary>The size threshold only means anything once a cheaper identity method is chosen, so it is
    /// hidden — not disabled — under the default FullHash. Like <see cref="ShowMirrorDeletion"/> the value
    /// is never forced, so switching away and back does not lose what the user typed.</summary>
    public bool ShowLargeFileIdentityThreshold => LargeFileIdentity != LargeFileIdentity.FullHash;

    // Fully-qualified param type: the property is also named LargeFileIdentity, so the unqualified name
    // would bind to the property rather than the enum here (mirrors OnSyncModeChanged).
    partial void OnLargeFileIdentityChanged(global::FileManager.Contracts.Profiles.LargeFileIdentity value) =>
        OnPropertyChanged(nameof(ShowLargeFileIdentityThreshold));

    /// <summary>The destination sweep is optional in AdditiveArchive but mandatory in Mirror (its
    /// only source of Deleted-orphan previews), so the checkbox is locked on in Mirror.</summary>
    public bool CanEditScanDestination => SyncMode != SyncMode.Mirror;

    /// <summary>Only Mirror deletes destination files, so the timing choice is hidden — not disabled —
    /// outside it. Unlike <see cref="ScanDestination"/> the value is never forced: it stays as the user
    /// left it and simply goes inert, so switching away and back does not lose the choice.</summary>
    public bool ShowMirrorDeletion => SyncMode == SyncMode.Mirror;

    // Fully-qualified param type: the property is also named SyncMode, so the unqualified name would
    // bind to the property, not the enum, in this position (mirrors OnConcurrencyModeChanged).
    partial void OnSyncModeChanged(global::FileManager.Contracts.Profiles.SyncMode value)
    {
        // Loads set SyncMode and ScanDestination explicitly; only respond to genuine user switches. An
        // in-flight undo/redo is restoring both properties itself, so re-applying the rule here would
        // fight the restore and overwrite the ScanDestination value being put back.
        if (!_loading && !History.IsRestoring)
        {
            // Everything recorded here joins the mode's own step, because the batch bracketing this
            // property's notification is already open (see OnPropertyChanging below).
            bool previousPreference = _scanDestinationPreference;
            if (value == SyncMode.Mirror)
            {
                _scanDestinationPreference = ScanDestination;   // remember the AdditiveArchive choice
                ScanDestination = true;                          // Mirror forces the sweep on
            }
            else
            {
                ScanDestination = _scanDestinationPreference;     // restore it when leaving Mirror
            }

            // The preference is a plain field, invisible to the property recorder, so it needs its two
            // halves spelled out. Without this, Mirror → undo → Mirror forgets the AdditiveArchive
            // choice the first switch stashed.
            bool newPreference = _scanDestinationPreference;
            if (newPreference != previousPreference)
            {
                History.Record(
                    undo: () => _scanDestinationPreference = previousPreference,
                    redo: () => _scanDestinationPreference = newPreference);
            }
        }
        OnPropertyChanged(nameof(CanEditScanDestination));
        OnPropertyChanged(nameof(ShowMirrorDeletion));
    }

    /// <summary>Opens an undo batch around a <see cref="SyncMode"/> switch, so the mode change and the
    /// <see cref="ScanDestination"/> write it forces undo together.
    ///
    /// The bracket has to straddle the notification rather than sit inside
    /// <see cref="OnSyncModeChanged"/>: the generated setter runs that hook BEFORE raising
    /// PropertyChanged, so a batch opened and closed within it would capture the coupled write and leave
    /// the mode's own step outside — one undo would then revert the mode while leaving the sweep flag
    /// the mode had forced on. Unconditional because an empty batch records nothing, so a load or an
    /// in-flight restore (both of which record nothing) costs only the scope.</summary>
    protected override void OnPropertyChanging(PropertyChangingEventArgs e)
    {
        base.OnPropertyChanging(e);
        if (e.PropertyName is nameof(SyncMode))
            _syncModeBatch = History.Batch();
    }

    /// <inheritdoc cref="OnPropertyChanging"/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        // Base first: raising the event is what pushes this property's own step, and it has to land
        // inside the batch before the batch closes.
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SyncMode))
        {
            _syncModeBatch?.Dispose();
            _syncModeBatch = null;
        }
    }

    // ============================ Undo / redo and unsaved changes ============================

    /// <summary>Undo/redo for this editing session, and the source of <see cref="IsDirty"/>. Bound
    /// directly by the Undo/Redo buttons on the Profile tab header; the Ctrl+Z / Ctrl+Y shortcuts in
    /// <c>ProfileEditorView</c> run the same commands. Lives as long as the editor and is reset by every
    /// load, so it never spans two profiles.</summary>
    public UndoHistory History { get; } = new();

    /// <summary>The reversible draft fields, one line each. Coalesced for values a user builds up a
    /// keystroke at a time (names, paths, the glob boxes, the numeric text fields); not coalesced for
    /// the enums and booleans, where every pick is a decision that deserves its own step.
    ///
    /// The omissions are deliberate, and are the same set the old dirty-flag override excluded — now
    /// inverted into an explicit opt-in. <see cref="HasProfile"/> and <see cref="IsNew"/> say which
    /// profile is open, not what was edited. <see cref="CanAcknowledgeAndSave"/>,
    /// <see cref="LocalError"/>, <see cref="StatusMessage"/> and <see cref="ShowUnsavedWarning"/> are
    /// transient chrome. <see cref="ShowArchiveFolder"/>, <see cref="CanEditScanDestination"/> and
    /// <see cref="ShowMirrorDeletion"/> are derived from properties that are already tracked, so
    /// recording them would double a step.</summary>
    public IEnumerable<UndoableProperty> UndoableProperties =>
    [
        UndoableProperty.For(nameof(ProfileName), () => ProfileName, v => ProfileName = v, coalesce: true),
        UndoableProperty.For(nameof(Active), () => Active, v => Active = v),
        UndoableProperty.For(nameof(SyncMode), () => SyncMode, v => SyncMode = v),
        UndoableProperty.For(nameof(ScanDestination), () => ScanDestination, v => ScanDestination = v),
        UndoableProperty.For(nameof(TargetLayout), () => TargetLayout, v => TargetLayout = v),
        UndoableProperty.For(nameof(ConflictResolution), () => ConflictResolution, v => ConflictResolution = v),
        UndoableProperty.For(nameof(OverwriteHandling), () => OverwriteHandling, v => OverwriteHandling = v),
        UndoableProperty.For(nameof(MirrorDeletion), () => MirrorDeletion, v => MirrorDeletion = v),
        UndoableProperty.For(nameof(VerificationMethod), () => VerificationMethod, v => VerificationMethod = v),
        UndoableProperty.For(nameof(LargeFileIdentity), () => LargeFileIdentity, v => LargeFileIdentity = v),
        UndoableProperty.For(nameof(LargeFileIdentityThresholdText), () => LargeFileIdentityThresholdText,
            v => LargeFileIdentityThresholdText = v, coalesce: true),
        UndoableProperty.For(nameof(OnSuccess), () => OnSuccess, v => OnSuccess = v),
        UndoableProperty.For(nameof(ArchiveFolder), () => ArchiveFolder, v => ArchiveFolder = v, coalesce: true),
        UndoableProperty.For(nameof(MetadataOnConflict), () => MetadataOnConflict, v => MetadataOnConflict = v),
        UndoableProperty.For(nameof(Verbosity), () => Verbosity, v => Verbosity = v),
        UndoableProperty.For(nameof(NotifyOnFailure), () => NotifyOnFailure, v => NotifyOnFailure = v),
        UndoableProperty.For(nameof(IncludeGlobsText), () => IncludeGlobsText, v => IncludeGlobsText = v, coalesce: true),
        UndoableProperty.For(nameof(ExcludeGlobsText), () => ExcludeGlobsText, v => ExcludeGlobsText = v, coalesce: true),
        UndoableProperty.For(nameof(MinSizeText), () => MinSizeText, v => MinSizeText = v, coalesce: true),
        UndoableProperty.For(nameof(MaxSizeText), () => MaxSizeText, v => MaxSizeText = v, coalesce: true),
        UndoableProperty.For(nameof(MaxDepthText), () => MaxDepthText, v => MaxDepthText = v, coalesce: true),
    ];

    /// <summary>Adding, removing and editing source/target rows all become undo steps; the row view
    /// models declare their own properties and the history picks them up as rows enter the lists.
    ///
    /// <see cref="Issues"/> is deliberately absent: it is validation output from the service, not user
    /// state, and a save that returns warnings must not land on the undo stack.</summary>
    public void TrackNested(UndoHistory history)
    {
        history.TrackCollection(Sources);
        history.TrackCollection(Targets);
    }

    private void OnHistoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The shell drives the sidebar's unsaved marker and its row locking off this notification, and
        // the Discard button's visibility binds to it, so it has to be re-raised from here.
        if (e.PropertyName is nameof(UndoHistory.IsDirty))
            OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>True when there are edits Save has not persisted. Position-based, from the undo history:
    /// undoing back to the last saved position clears it, which the old latching flag could not do.</summary>
    public bool IsDirty => History.IsDirty;

    /// <summary>Opens a load: recording off and the coupled-property hooks disarmed for the duration,
    /// and on close the validation issues and banners cleared and the undo history dropped.
    ///
    /// EVERY path that changes which profile the draft holds must go through this. The history's steps
    /// capture this view model — and there is exactly one of it, reused for every profile — so a step that
    /// outlived a load would apply the previous profile's values to the new one's draft on the next undo.
    /// Dropping the history here is the whole reason undo cannot escape the profile on screen, so it is a
    /// scope rather than three call sites that each have to remember it.
    ///
    /// <see cref="_loading"/> is held alongside <see cref="UndoHistory.Suppress"/> because the two do
    /// different jobs: Suppress stops the history recording, while <c>_loading</c> stops the SyncMode hook
    /// from applying the Mirror rule to values being loaded.</summary>
    private IDisposable BeginLoad()
    {
        IDisposable suppressed = History.Suppress();
        _loading = true;
        return new LoadScope(this, suppressed);
    }

    private sealed class LoadScope(ProfileEditorViewModel editor, IDisposable suppressed) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            editor.Issues.Clear();
            editor.ResetTransientState();
            editor._loading = false;
            suppressed.Dispose();
            editor.History.Reset();   // the loaded draft is the baseline: nothing to undo, nothing unsaved
        }
    }

    public void LoadNew()
    {
        using IDisposable load = BeginLoad();
        _original = null;
        IsNew = true;
        HasProfile = true;
        ProfileName = "New Profile";
        Active = true;
        SyncMode = SyncMode.AdditiveArchive;
        ScanDestination = false;
        _scanDestinationPreference = false;
        TargetLayout = TargetLayout.PreserveStructure;
        ConflictResolution = ConflictResolution.Skip;              // safest default
        OverwriteHandling = OverwriteHandling.StageOverwrites;
        MirrorDeletion = MirrorDeletion.AfterCopy;                 // safest default
        VerificationMethod = VerificationMethod.XxHash128;
        LargeFileIdentity = LargeFileIdentity.FullHash;            // exact, and the pre-existing behaviour
        LargeFileIdentityThresholdText = PolicySettings.DefaultLargeFileIdentityThresholdBytes.ToString();
        OnSuccess = OnSuccessAction.KeepSource;
        ArchiveFolder = "";
        MetadataOnConflict = MetadataOnConflict.WarnAndContinue;
        Verbosity = LogVerbosity.FailuresAndSkips;
        NotifyOnFailure = true;
        IncludeGlobsText = "";
        ExcludeGlobsText = "";
        MinSizeText = "";
        MaxSizeText = "";
        MaxDepthText = "";
        Sources.Clear();
        Sources.Add(NewSourceRow());
        Targets.Clear();
        Targets.Add(NewTargetRow());
    }

    public void Load(Profile profile)
    {
        using IDisposable load = BeginLoad();
        _original = profile;
        IsNew = false;
        HasProfile = true;
        ProfileName = profile.Name;
        Active = profile.Active;
        SyncMode = profile.SyncMode;
        // Mirror always scans; otherwise honor the stored preference. Remember the raw preference so a
        // later Mirror → AdditiveArchive switch restores it.
        _scanDestinationPreference = profile.ScanDestination;
        ScanDestination = profile.EffectiveScanDestination;
        TargetLayout = profile.TargetLayout;
        ConflictResolution = profile.Policies.ConflictResolution;
        OverwriteHandling = profile.Policies.OverwriteHandling;
        MirrorDeletion = profile.Policies.MirrorDeletion;
        VerificationMethod = profile.Policies.VerificationMethod;
        LargeFileIdentity = profile.Policies.LargeFileIdentity;
        LargeFileIdentityThresholdText = profile.Policies.LargeFileIdentityThresholdBytes.ToString();
        OnSuccess = profile.Policies.OnSuccess;
        ArchiveFolder = profile.Policies.ArchiveFolder ?? "";
        MetadataOnConflict = profile.Policies.MetadataOnConflict;
        Verbosity = profile.Logging.Verbosity;
        NotifyOnFailure = profile.Logging.NotifyOnFailure;
        IncludeGlobsText = JoinLines(profile.Filters?.Include);
        ExcludeGlobsText = JoinLines(profile.Filters?.ExcludeGlob);
        MinSizeText = profile.Filters?.MinSizeBytes?.ToString() ?? "";
        MaxSizeText = profile.Filters?.MaxSizeBytes?.ToString() ?? "";
        MaxDepthText = profile.Filters?.MaxDepth?.ToString() ?? "";
        Sources.Clear();
        foreach (SourceConfig source in profile.Sources)
        {
            SourceRowViewModel row = NewSourceRow();
            row.Path = source.Path;
            row.SettleDelaySeconds = source.SettleDelaySeconds;
            row.StabilityIntervalMs = source.StabilityIntervalMs;
            Sources.Add(row);
        }
        Targets.Clear();
        foreach (TargetConfig target in profile.Targets)
        {
            TargetRowViewModel row = NewTargetRow();
            row.Path = target.Path;
            Targets.Add(row);
        }
    }

    public void Clear()
    {
        using IDisposable load = BeginLoad();
        _original = null;
        HasProfile = false;
        IsNew = false;
        Sources.Clear();
        Targets.Clear();
    }

    /// <summary>Rebuilds the immutable record. Unowned fields round-trip from the original:
    /// Transformers always; Triggers preserved for existing profiles (so a schedule created by
    /// hand-edited JSON is not silently destroyed), fixed to manual-only for new ones.</summary>
    public Profile BuildProfile()
    {
        FilterSet? filters = BuildFilters();
        return new Profile
        {
            SchemaVersion = _original?.SchemaVersion ?? CurrentSchemaVersion,
            Id = _original?.Id ?? Guid.NewGuid(),
            Name = ProfileName.Trim(),
            Active = Active,
            SyncMode = SyncMode,
            // The observable already reflects the forced state (Mirror → true via OnSyncModeChanged),
            // and the engine recomputes EffectiveScanDestination, so store the checkbox value as-is.
            ScanDestination = ScanDestination,
            TargetLayout = TargetLayout,
            Triggers = _original?.Triggers ?? new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
            Sources = Sources.Select(s => new SourceConfig
            {
                Path = s.Path.Trim(),
                SettleDelaySeconds = s.SettleDelaySeconds,
                StabilityIntervalMs = s.StabilityIntervalMs,
                Filters = _original?.Sources.FirstOrDefault(o => PathsEqual(o.Path, s.Path))?.Filters,
            }).ToList(),
            Transformers = _original?.Transformers,
            Targets = Targets.Select(t => new TargetConfig { Path = t.Path.Trim() }).ToList(),
            Policies = new PolicySettings
            {
                ConflictResolution = ConflictResolution,
                OverwriteHandling = OverwriteHandling,
                VerificationMethod = VerificationMethod,
                LargeFileIdentity = LargeFileIdentity,
                LargeFileIdentityThresholdBytes = ParsedIdentityThreshold(),
                OnSuccess = OnSuccess,
                ArchiveFolder = string.IsNullOrWhiteSpace(ArchiveFolder) ? null : ArchiveFolder.Trim(),
                OnFailure = OnFailureAction.AbortRestoreAndClean,
                MetadataOnConflict = MetadataOnConflict,
                // Stored even outside Mirror (where it is inert), so switching a profile to Mirror
                // later finds the choice the user last made rather than a silent reset.
                MirrorDeletion = MirrorDeletion,
            },
            Filters = filters,
            Logging = new LoggingSettings { Verbosity = Verbosity, NotifyOnFailure = NotifyOnFailure },
        };
    }

    [RelayCommand]
    public Task SaveAsync() => SaveCoreAsync(acknowledgeWarnings: false);

    /// <summary>The loud "I understand — save anyway" path for blocking warnings
    /// (e.g. PROFILE_UNVERIFIED_DELETE).</summary>
    [RelayCommand]
    public Task AcknowledgeAndSaveAsync() => SaveCoreAsync(acknowledgeWarnings: true);

    [RelayCommand]
    public void AddSource() => Sources.Add(NewSourceRow());

    [RelayCommand]
    public void RemoveSource(SourceRowViewModel? row)
    {
        if (row is not null)
            Sources.Remove(row);
    }

    [RelayCommand]
    public void AddTarget() => Targets.Add(NewTargetRow());

    [RelayCommand]
    public void RemoveTarget(TargetRowViewModel? row)
    {
        if (row is not null)
            Targets.Remove(row);
    }

    [RelayCommand]
    public void Discard()
    {
        if (_original is { } original)
            Load(original);
        else if (IsNew)
            LoadNew();
        else
            Clear();
        Discarded?.Invoke();
    }

    /// <summary>Builds the current draft for a dry run without saving. Parses the local numeric
    /// fields first (BuildProfile → BuildFilters silently drops parse failures), so an invalid Max
    /// size/depth surfaces as an error here instead of a half-built draft going to the service.</summary>
    public bool TryBuildDraft(out Profile? draft, out string? error)
    {
        draft = null;
        if (!TryParseLocalFields(out error))
            return false;
        draft = BuildProfile();
        error = null;
        return true;
    }

    private async Task SaveCoreAsync(bool acknowledgeWarnings)
    {
        try
        {
            StatusMessage = null;
            if (!TryParseLocalFields(out string? parseError))
            {
                LocalError = parseError;
                return;
            }
            LocalError = null;

            Profile draft = BuildProfile();
            var outcome = await _gateway.SaveProfileAsync(draft, acknowledgeWarnings);
            if (outcome.TryGetError(out IpcError? error))
            {
                LocalError = $"Save failed: {error.Message}";
                return;
            }
            outcome.TryGetValue(out SaveOutcome? result);

            Issues.Clear();
            foreach (ValidationIssue issue in result!.Issues)
                Issues.Add(new ValidationIssueItem(issue.Severity, issue.Code, issue.Message));

            if (result.Saved)
            {
                _original = draft;
                IsNew = false;
                // Clears the unsaved-changes flag without dropping the history, so the user can still
                // step back through what they just saved.
                History.MarkSaved();
                ShowUnsavedWarning = false;
                CanAcknowledgeAndSave = false;
                StatusMessage = Issues.Count > 0 ? "Saved (with warnings)." : "Saved.";
                Saved?.Invoke(draft.Id);
            }
            else
            {
                CanAcknowledgeAndSave =
                    Issues.Any(i => i.IsBlockingWarning) && Issues.All(i => !i.IsError);
            }
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a logged error banner instead of an
            // unobserved command fault.
            Serilog.Log.Error(ex, "Profile save failed unexpectedly");
            LocalError = $"Save failed unexpectedly: {ex.Message}";
        }
    }

    private bool TryParseLocalFields(out string? error)
    {
        if (!TryParseOptionalLong(MinSizeText, "Min size", out _, out error) ||
            !TryParseOptionalLong(MaxSizeText, "Max size", out _, out error) ||
            !TryParseOptionalInt(MaxDepthText, "Max depth", out _, out error))
            return false;

        // Unlike the filter sizes this one is not optional — it always has a value, so an empty or
        // unparseable box is rejected rather than silently becoming "no threshold" (which would read as
        // "treat every file as large", the dangerous direction).
        if (!long.TryParse(LargeFileIdentityThresholdText.Trim(), out long threshold) || threshold < 0)
        {
            error = "Large-file identity threshold must be a non-negative whole number of bytes.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>The parsed threshold, for <c>BuildProfile</c>. Only reached after
    /// <see cref="TryParseLocalFields"/> has accepted the text, so the fallback is unreachable in
    /// practice — it exists so a future caller cannot turn a parse slip into a 0 threshold.</summary>
    private long ParsedIdentityThreshold() =>
        long.TryParse(LargeFileIdentityThresholdText.Trim(), out long parsed) && parsed >= 0
            ? parsed
            : PolicySettings.DefaultLargeFileIdentityThresholdBytes;

    private FilterSet? BuildFilters()
    {
        IReadOnlyList<string>? include = SplitLines(IncludeGlobsText);
        IReadOnlyList<string>? exclude = SplitLines(ExcludeGlobsText);
        TryParseOptionalLong(MinSizeText, "Min size", out long? minSize, out _);
        TryParseOptionalLong(MaxSizeText, "Max size", out long? maxSize, out _);
        TryParseOptionalInt(MaxDepthText, "Max depth", out int? maxDepth, out _);

        // Fields this editor does not surface (regex, age, attributes) round-trip from the original.
        FilterSet? original = _original?.Filters;
        FilterSet built = new()
        {
            Include = include,
            ExcludeGlob = exclude,
            IncludeRegex = original?.IncludeRegex,
            ExcludeRegex = original?.ExcludeRegex,
            MinSizeBytes = minSize,
            MaxSizeBytes = maxSize,
            ModifiedWithin = original?.ModifiedWithin,
            ModifiedOlderThan = original?.ModifiedOlderThan,
            CreatedWithin = original?.CreatedWithin,
            Attributes = original?.Attributes,
            MaxDepth = maxDepth,
            ContentHashDedupe = original?.ContentHashDedupe ?? false,
        };

        bool empty = built.Include is null && built.ExcludeGlob is null && built.IncludeRegex is null
            && built.ExcludeRegex is null && built.MinSizeBytes is null && built.MaxSizeBytes is null
            && built.ModifiedWithin is null && built.ModifiedOlderThan is null && built.CreatedWithin is null
            && built.Attributes is null && built.MaxDepth is null && !built.ContentHashDedupe;
        return empty ? null : built;
    }

    internal static IReadOnlyList<string>? SplitLines(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines;
    }

    private static string JoinLines(IReadOnlyList<string>? lines) =>
        lines is null ? "" : string.Join('\n', lines);

    private static bool TryParseOptionalLong(string text, string label, out long? value, out string? error)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;
            return true;
        }
        if (long.TryParse(text.Trim(), out long parsed) && parsed >= 0)
        {
            value = parsed;
            error = null;
            return true;
        }
        error = $"{label} must be a non-negative whole number of bytes (or empty).";
        return false;
    }

    private static bool TryParseOptionalInt(string text, string label, out int? value, out string? error)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = null;
            return true;
        }
        if (int.TryParse(text.Trim(), out int parsed) && parsed >= 0)
        {
            value = parsed;
            error = null;
            return true;
        }
        error = $"{label} must be a non-negative whole number (or empty).";
        return false;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    // Rows need no undo wiring here: the history tracks them as they enter Sources/Targets and untracks
    // them as they leave, so a row is recorded from the moment it is added.
    private SourceRowViewModel NewSourceRow()
    {
        SourceRowViewModel row = new(_folderPicker);
        row.PreviousRowPath = () => PathAbove(Sources, row, r => r.Path);
        return row;
    }

    private TargetRowViewModel NewTargetRow()
    {
        TargetRowViewModel row = new(_folderPicker);
        row.PreviousRowPath = () => PathAbove(Targets, row, r => r.Path);
        return row;
    }

    /// <summary>The nearest non-empty path ABOVE <paramref name="row"/>, or null when there is none.
    /// Walks past empty rows rather than stopping at the immediate predecessor: a blank row in
    /// between must not dead-end the chain. Resolved at click time, so the row's position (and
    /// whether it is still in the collection at all) is always the current one.</summary>
    private static string? PathAbove<T>(IList<T> rows, T row, Func<T, string> path) where T : class
    {
        for (int i = rows.IndexOf(row) - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(path(rows[i])))
                return path(rows[i]);
        }
        return null;
    }

    /// <summary>Clears the banners and acknowledgement state. The unsaved-changes flag is not among them
    /// any more — it comes from the history, which every caller resets right after this.</summary>
    private void ResetTransientState()
    {
        CanAcknowledgeAndSave = false;
        LocalError = null;
        StatusMessage = null;
        ShowUnsavedWarning = false;
    }
}
