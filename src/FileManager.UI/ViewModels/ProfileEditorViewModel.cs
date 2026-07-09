using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using FileManager.UI.ViewModels.Editor;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Maps the immutable Profile record to a mutable draft and back. Fields the editor
/// does not own (Transformers, Triggers of an existing profile, Id, SchemaVersion) round-trip
/// through the stashed original untouched.</summary>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    /// <summary>Spec §5.1: the schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 2;

    private readonly IIpcGateway _gateway;
    private readonly IFolderPicker _folderPicker;
    private Profile? _original;
    private bool _loading;

    public ProfileEditorViewModel(IIpcGateway gateway, IFolderPicker folderPicker)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
        Sources.CollectionChanged += OnRowsChanged;
        Targets.CollectionChanged += OnRowsChanged;
    }

    /// <summary>Invoked after a successful save so the shell can refresh the list.</summary>
    public Action<Guid>? Saved { get; set; }

    // ----- enum options (static arrays: AOT-safe, no Enum.GetValues reflection) -----
    public IReadOnlyList<SyncMode> SyncModeOptions { get; } = [SyncMode.AdditiveArchive];   // Mirror is [reserved]
    public IReadOnlyList<TargetLayout> TargetLayoutOptions { get; } = [TargetLayout.PreserveStructure, TargetLayout.Flatten];
    public IReadOnlyList<ConflictResolution> ConflictResolutionOptions { get; } =
        [ConflictResolution.Skip, ConflictResolution.RenameSuffix, ConflictResolution.Overwrite, ConflictResolution.OverwriteIfNewer];
    public IReadOnlyList<OverwriteHandling> OverwriteHandlingOptions { get; } =
        [OverwriteHandling.StageOverwrites, OverwriteHandling.DirectOverwrite];
    public IReadOnlyList<VerificationMethod> VerificationOptions { get; } =
        [VerificationMethod.Sha256, VerificationMethod.None];                               // SizeTimestamp is [reserved]
    public IReadOnlyList<OnSuccessAction> OnSuccessOptions { get; } =
        [OnSuccessAction.KeepSource, OnSuccessAction.MoveToArchive, OnSuccessAction.MoveToTrash, OnSuccessAction.PermanentDelete];
    public IReadOnlyList<MetadataOnConflict> MetadataOptions { get; } =
        [MetadataOnConflict.WarnAndContinue, MetadataOnConflict.FailJob];
    public IReadOnlyList<LogVerbosity> VerbosityOptions { get; } =
        [LogVerbosity.FailuresOnly, LogVerbosity.FailuresAndSkips, LogVerbosity.All];
    // Per-profile scope offers Inherit (defer to the global setting) in addition to Automatic/Manual.
    public IReadOnlyList<ConcurrencyMode> ConcurrencyModeOptions { get; } =
        [ConcurrencyMode.Inherit, ConcurrencyMode.Automatic, ConcurrencyMode.Manual];

    // ----- draft state -----
    [ObservableProperty] public partial bool HasProfile { get; set; }
    [ObservableProperty] public partial bool IsNew { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; } = "";
    [ObservableProperty] public partial bool Active { get; set; } = true;
    [ObservableProperty] public partial SyncMode SyncMode { get; set; } = SyncMode.AdditiveArchive;
    [ObservableProperty] public partial TargetLayout TargetLayout { get; set; } = TargetLayout.PreserveStructure;
    [ObservableProperty] public partial ConflictResolution ConflictResolution { get; set; } = ConflictResolution.Skip;
    [ObservableProperty] public partial OverwriteHandling OverwriteHandling { get; set; } = OverwriteHandling.StageOverwrites;
    [ObservableProperty] public partial VerificationMethod VerificationMethod { get; set; } = VerificationMethod.Sha256;
    [ObservableProperty] public partial OnSuccessAction OnSuccess { get; set; } = OnSuccessAction.KeepSource;
    [ObservableProperty] public partial string ArchiveFolder { get; set; } = "";
    [ObservableProperty] public partial MetadataOnConflict MetadataOnConflict { get; set; } = MetadataOnConflict.WarnAndContinue;
    [ObservableProperty] public partial LogVerbosity Verbosity { get; set; } = LogVerbosity.FailuresAndSkips;
    [ObservableProperty] public partial bool NotifyOnFailure { get; set; } = true;
    [ObservableProperty] public partial ConcurrencyMode ConcurrencyMode { get; set; } = ConcurrencyMode.Inherit;
    [ObservableProperty] public partial int ConcurrencyWorkers { get; set; } = 1;

    /// <summary>One glob per line.</summary>
    [ObservableProperty] public partial string IncludeGlobsText { get; set; } = "";
    [ObservableProperty] public partial string ExcludeGlobsText { get; set; } = "";
    [ObservableProperty] public partial string MinSizeText { get; set; } = "";
    [ObservableProperty] public partial string MaxSizeText { get; set; } = "";
    [ObservableProperty] public partial string MaxDepthText { get; set; } = "";

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<TargetRowViewModel> Targets { get; } = [];
    public ObservableCollection<ValidationIssueItem> Issues { get; } = [];

    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] public partial bool CanAcknowledgeAndSave { get; set; }
    [ObservableProperty] public partial string? LocalError { get; set; }
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial bool ShowUnsavedWarning { get; set; }

    public bool ShowArchiveFolder => OnSuccess == OnSuccessAction.MoveToArchive;

    partial void OnOnSuccessChanged(OnSuccessAction value) => OnPropertyChanged(nameof(ShowArchiveFolder));

    public bool ShowConcurrencyWorkers => ConcurrencyMode == ConcurrencyMode.Manual;

    // Fully-qualified param type: the property is also named ConcurrencyMode, so the unqualified
    // name would bind to the property, not the enum, in this position.
    partial void OnConcurrencyModeChanged(global::FileManager.Contracts.Profiles.ConcurrencyMode value) =>
        OnPropertyChanged(nameof(ShowConcurrencyWorkers));

    /// <summary>Editor mutations mark the draft dirty; loads do not.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
            return;
        switch (e.PropertyName)
        {
            case nameof(IsDirty):
            case nameof(CanAcknowledgeAndSave):
            case nameof(LocalError):
            case nameof(StatusMessage):
            case nameof(ShowUnsavedWarning):
            case nameof(HasProfile):
            case nameof(IsNew):
            case nameof(ShowArchiveFolder):
            case nameof(ShowConcurrencyWorkers):
                return;
            default:
                IsDirty = true;
                return;
        }
    }

    public void LoadNew()
    {
        _loading = true;
        _original = null;
        IsNew = true;
        HasProfile = true;
        ProfileName = "New Profile";
        Active = true;
        SyncMode = SyncMode.AdditiveArchive;
        TargetLayout = TargetLayout.PreserveStructure;
        ConflictResolution = ConflictResolution.Skip;              // safest default
        OverwriteHandling = OverwriteHandling.StageOverwrites;
        VerificationMethod = VerificationMethod.Sha256;
        OnSuccess = OnSuccessAction.KeepSource;
        ArchiveFolder = "";
        MetadataOnConflict = MetadataOnConflict.WarnAndContinue;
        Verbosity = LogVerbosity.FailuresAndSkips;
        NotifyOnFailure = true;
        ConcurrencyMode = ConcurrencyMode.Inherit;
        ConcurrencyWorkers = 1;
        IncludeGlobsText = "";
        ExcludeGlobsText = "";
        MinSizeText = "";
        MaxSizeText = "";
        MaxDepthText = "";
        Sources.Clear();
        Sources.Add(NewSourceRow());
        Targets.Clear();
        Targets.Add(NewTargetRow());
        Issues.Clear();
        ResetTransientState();
        _loading = false;
    }

    public void Load(Profile profile)
    {
        _loading = true;
        _original = profile;
        IsNew = false;
        HasProfile = true;
        ProfileName = profile.Name;
        Active = profile.Active;
        SyncMode = profile.SyncMode;
        TargetLayout = profile.TargetLayout;
        ConflictResolution = profile.Policies.ConflictResolution;
        OverwriteHandling = profile.Policies.OverwriteHandling;
        VerificationMethod = profile.Policies.VerificationMethod;
        OnSuccess = profile.Policies.OnSuccess;
        ArchiveFolder = profile.Policies.ArchiveFolder ?? "";
        MetadataOnConflict = profile.Policies.MetadataOnConflict;
        Verbosity = profile.Logging.Verbosity;
        NotifyOnFailure = profile.Logging.NotifyOnFailure;
        ConcurrencyMode = profile.Concurrency.Mode;
        ConcurrencyWorkers = profile.Concurrency.ManualWorkers ?? 1;
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
        Issues.Clear();
        ResetTransientState();
        _loading = false;
    }

    public void Clear()
    {
        _loading = true;
        _original = null;
        HasProfile = false;
        IsNew = false;
        Sources.Clear();
        Targets.Clear();
        Issues.Clear();
        ResetTransientState();
        _loading = false;
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
                OnSuccess = OnSuccess,
                ArchiveFolder = string.IsNullOrWhiteSpace(ArchiveFolder) ? null : ArchiveFolder.Trim(),
                OnFailure = OnFailureAction.AbortRestoreAndClean,
                MetadataOnConflict = MetadataOnConflict,
            },
            Filters = filters,
            Logging = new LoggingSettings { Verbosity = Verbosity, NotifyOnFailure = NotifyOnFailure },
            Concurrency = new ConcurrencyOverride
            {
                Mode = ConcurrencyMode,
                ManualWorkers = ConcurrencyMode == ConcurrencyMode.Manual ? ConcurrencyWorkers : null,
            },
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
                IsDirty = false;
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
        error = null;
        return true;
    }

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

    private SourceRowViewModel NewSourceRow()
    {
        SourceRowViewModel row = new(_folderPicker);
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private TargetRowViewModel NewTargetRow()
    {
        TargetRowViewModel row = new(_folderPicker);
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_loading)
            IsDirty = true;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_loading)
            IsDirty = true;
    }

    private void ResetTransientState()
    {
        IsDirty = false;
        CanAcknowledgeAndSave = false;
        LocalError = null;
        StatusMessage = null;
        ShowUnsavedWarning = false;
    }
}
