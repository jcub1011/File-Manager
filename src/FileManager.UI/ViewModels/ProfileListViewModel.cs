using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileManager.UI.ViewModels;

/// <summary>One row of the profile list. Rows are replaced wholesale on refresh.</summary>
public sealed record ProfileListItem(Guid ProfileId, string Name, bool Active, string TriggerSummary)
{
    public string ActiveText => Active ? "Active" : "Inactive";

    /// <summary>Two-letter badge shown in the collapsed sidebar rail (see <see cref="ProfileAcronym"/>).</summary>
    public string Acronym => ProfileAcronym.From(Name);

    /// <summary>Bound by the row's right-click "Export" menu item in both the list and the collapsed
    /// rail. Stamped onto every row from <see cref="ProfileListViewModel.ExportProfileCommand"/> when
    /// the list is built, so the popup binds directly to its own DataContext (no cross-namescope
    /// ancestor lookup). Takes this row as its parameter.</summary>
    public ICommand? ExportCommand { get; init; }

    /// <summary>Bound by the row's right-click "Preview…" menu item, stamped the same way as
    /// <see cref="ExportCommand"/>. Takes this row as its parameter. It opens the profile and previews
    /// what a run would do; nothing moves until the user approves the plan on the Preview tab.</summary>
    public ICommand? RunCommand { get; init; }

    /// <summary>Bound by the row's right-click "Delete…" menu item, stamped the same way as
    /// <see cref="ExportCommand"/>. Takes this row as its parameter. The shell confirms modally
    /// first — the row itself carries no pending-delete state.</summary>
    public ICommand? DeleteCommand { get; init; }
}

public sealed partial class ProfileListViewModel(IIpcGateway gateway) : ViewModelBase
{
    /// <summary>How long to wait after the last keystroke before applying the search filter.
    /// Mutable as a test seam: tests set it to zero so they need not wait real time.</summary>
    internal TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(250);

    private bool _revertingSelection;
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>Test seam: the in-flight (or last-completed) debounced filter pass, so tests can
    /// await it deterministically instead of sleeping.</summary>
    internal Task? PendingSearch { get; private set; }

    /// <summary>The full, unfiltered set of profiles (source of truth). The list UI binds to
    /// <see cref="FilteredProfiles"/>, which is derived from this via <see cref="SearchText"/>.</summary>
    public ObservableCollection<ProfileListItem> Profiles { get; } = [];

    /// <summary>The subset of <see cref="Profiles"/> shown in the list: those whose name matches
    /// <see cref="SearchText"/>, plus the currently selected profile (so filtering never drops the
    /// active selection out from under the editor).</summary>
    public ObservableCollection<ProfileListItem> FilteredProfiles { get; } = [];

    /// <summary>Set by the shell to the "new profile" command. Bound by the collapsed rail's add
    /// tile so it can start a new profile without the rail needing the shell view model in scope.</summary>
    public IRelayCommand? CreateProfileCommand { get; set; }

    /// <summary>Set by the shell to its single-profile export command (takes a <see cref="ProfileListItem"/>).
    /// Stamped onto every row's <see cref="ProfileListItem.ExportCommand"/> in <see cref="RefreshAsync"/>
    /// so the right-click "Export" menu works in both the list and the collapsed rail.</summary>
    public ICommand? ExportProfileCommand { get; set; }

    /// <summary>Set by the shell to its manual-run command (takes a <see cref="ProfileListItem"/>),
    /// stamped onto every row's <see cref="ProfileListItem.RunCommand"/> the same way.</summary>
    public ICommand? RunProfileCommand { get; set; }

    /// <summary>Set by the shell to its confirming delete command (takes a <see cref="ProfileListItem"/>),
    /// stamped onto every row's <see cref="ProfileListItem.DeleteCommand"/> the same way.</summary>
    public ICommand? DeleteProfileCommand { get; set; }

    /// <summary>Whether any profiles exist at all (independent of the search filter). Drives the
    /// content-area empty state: false → "no profiles yet, create one"; true → "select a profile".
    /// Maintained by <see cref="RefreshAsync"/>, the sole mutation point of <see cref="Profiles"/>.</summary>
    [ObservableProperty]
    public partial bool HasProfiles { get; set; }

    /// <summary>Asked before honoring a selection change; false (dirty editor) reverts it.</summary>
    public Func<bool>? CanNavigate { get; set; }

    /// <summary>Raised after a selection change was allowed to stand.</summary>
    public Action<ProfileListItem?>? SelectionCommitted { get; set; }

    /// <summary>Raised when a selection change was blocked by CanNavigate.</summary>
    public Action? NavigationBlocked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(CanRunSelected))]
    public partial ProfileListItem? SelectedProfile { get; set; }

    /// <summary>Gates the Profile tab's Delete button: there must be a persisted profile to delete.
    /// A brand-new unsaved draft has no list row, so this is false for it.</summary>
    public bool CanDeleteSelected => SelectedProfile is not null;

    /// <summary>Gates the Profile tab's Run button. Mirrors the row menu's <c>IsEnabled="{Binding Active}"</c>:
    /// the engine refuses an inactive profile, so never offer the button for one.</summary>
    public bool CanRunSelected => SelectedProfile?.Active == true;

    /// <summary>Id of the profile the editor currently has unsaved edits for, or null. The sidebar
    /// row matching this id shows an unsaved-changes marker. Set by the shell from the editor's
    /// dirty state.</summary>
    [ObservableProperty]
    public partial Guid? UnsavedProfileId { get; set; }

    /// <summary>True while the editor has unsaved edits (new or existing). Every list row except the
    /// one being edited locks (becomes unselectable) until the draft is saved or discarded, so the
    /// user can't appear to navigate away. Set by the shell from the editor's dirty state.</summary>
    [ObservableProperty]
    public partial bool HasUnsavedChanges { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    partial void OnSelectedProfileChanged(ProfileListItem? oldValue, ProfileListItem? newValue)
    {
        if (_revertingSelection)
            return;
        if (CanNavigate?.Invoke() == false)
        {
            _revertingSelection = true;
            SelectedProfile = oldValue;
            _revertingSelection = false;
            NavigationBlocked?.Invoke();
            return;
        }
        SelectionCommitted?.Invoke(newValue);

        // A previously force-included (selected-but-unmatched) row must drop out now that the
        // selection has moved on. Such a row only exists while a search term is active, so skip the
        // rebuild otherwise: with no filter FilteredProfiles already equals Profiles, and clearing
        // it on every click would reset the ListBox scroll/selection visuals for nothing. Guarded so
        // the transient clear/re-add does not re-enter this handler as a real navigation.
        if (!string.IsNullOrEmpty(SearchText?.Trim()))
        {
            _revertingSelection = true;
            RebuildFiltered(newValue?.ProfileId);
            SelectedProfile = newValue;
            _revertingSelection = false;
        }
    }

    partial void OnSearchTextChanged(string? value)
    {
        // Debounce: coalesce rapid keystrokes into a single filter pass. Cancelling the previous
        // token abandons the pending delay; the continuation resumes on the UI thread (Avalonia's
        // SynchronizationContext) so touching the observable collections stays thread-safe. Dispose
        // the superseded source after cancelling it so we don't leak one CTS per keystroke.
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        PendingSearch = ApplySearchAfterDelayAsync(_searchDebounceCts.Token);
    }

    private async Task ApplySearchAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SearchDebounce, token);
        }
        catch (OperationCanceledException)
        {
            return;   // superseded by a newer keystroke
        }

        // This runs fire-and-forget, so an escaping exception would be unobserved; match the rest of
        // the view model and turn any failure into a logged error instead.
        try
        {
            Guid? keepId = SelectedProfile?.ProfileId;
            _revertingSelection = true;
            RebuildFiltered(keepId);
            // The clear/re-add above can null the list's selection; re-assert it (same instance, now
            // present in FilteredProfiles) so the editor stays on the open profile while filtering.
            SelectedProfile = keepId is Guid id ? Profiles.FirstOrDefault(p => p.ProfileId == id) : null;
            _revertingSelection = false;
        }
        catch (Exception ex)
        {
            _revertingSelection = false;
            Serilog.Log.Error(ex, "Applying the profile search filter failed");
        }
    }

    /// <summary>Repopulates <see cref="FilteredProfiles"/> from <see cref="Profiles"/>: keeps names
    /// matching <see cref="SearchText"/> (case-insensitive substring; empty term = all), and always
    /// keeps the profile identified by <paramref name="keepSelectedId"/> even if it doesn't match.</summary>
    private void RebuildFiltered(Guid? keepSelectedId)
    {
        string? term = SearchText?.Trim();
        FilteredProfiles.Clear();
        foreach (ProfileListItem p in Profiles)
        {
            bool matches = string.IsNullOrEmpty(term)
                || p.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (matches || p.ProfileId == keepSelectedId)
                FilteredProfiles.Add(p);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var listed = await gateway.ListProfilesAsync();
            if (listed.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not load profiles: {error.Message}";
                return;
            }
            listed.TryGetValue(out IReadOnlyList<ProfileSummary>? summaries);
            ErrorMessage = null;

            Guid? selectedId = SelectedProfile?.ProfileId;
            _revertingSelection = true;   // a refresh is not a user navigation
            Profiles.Clear();
            foreach (ProfileSummary summary in summaries!.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                Profiles.Add(new ProfileListItem(summary.ProfileId, summary.Name, summary.Active, summary.TriggerSummary)
                {
                    ExportCommand = ExportProfileCommand,
                    RunCommand = RunProfileCommand,
                    DeleteCommand = DeleteProfileCommand,
                });
            HasProfiles = Profiles.Count > 0;
            RebuildFiltered(selectedId);   // build the filtered view before re-selecting into it
            SelectedProfile = Profiles.FirstOrDefault(p => p.ProfileId == selectedId);
            _revertingSelection = false;
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a logged error banner instead of an
            // unobserved command fault.
            Serilog.Log.Error(ex, "Profile list refresh failed unexpectedly");
            ErrorMessage = $"Could not load profiles: {ex.Message}";
        }
    }

    /// <summary>Refresh and select a profile by id (after a save), without re-firing navigation.</summary>
    public async Task RefreshAndSelectAsync(Guid profileId)
    {
        await RefreshAsync();
        _revertingSelection = true;
        RebuildFiltered(profileId);   // ensure the target is present in the filtered view first
        SelectedProfile = Profiles.FirstOrDefault(p => p.ProfileId == profileId);
        _revertingSelection = false;
    }

    /// <summary>Deletes a profile. The confirmation is the shell's job (a modal, so it works from the
    /// collapsed rail too) — by the time this runs the user has already said yes.</summary>
    public async Task DeleteAsync(ProfileListItem doomed)
    {
        try
        {
            var deleted = await gateway.DeleteProfileAsync(doomed.ProfileId);
            if (deleted.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not delete \"{doomed.Name}\": {error.Message}";
                return;
            }
            if (SelectedProfile?.ProfileId == doomed.ProfileId)
                SelectedProfile = null;   // fires SelectionCommitted(null) → editor clears
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a logged error banner instead of an
            // unobserved command fault.
            Serilog.Log.Error(ex, "Profile delete failed unexpectedly");
            ErrorMessage = $"Could not delete \"{doomed.Name}\": {ex.Message}";
        }
    }
}
