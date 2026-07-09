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

namespace FileManager.UI.ViewModels;

/// <summary>One row of the profile list. Rows are replaced wholesale on refresh.</summary>
public sealed record ProfileListItem(Guid ProfileId, string Name, bool Active, string TriggerSummary)
{
    public string ActiveText => Active ? "Active" : "Inactive";
}

public sealed partial class ProfileListViewModel(IIpcGateway gateway) : ViewModelBase
{
    /// <summary>How long to wait after the last keystroke before applying the search filter.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private bool _revertingSelection;
    private CancellationTokenSource? _searchDebounceCts;

    /// <summary>The full, unfiltered set of profiles (source of truth). The list UI binds to
    /// <see cref="FilteredProfiles"/>, which is derived from this via <see cref="SearchText"/>.</summary>
    public ObservableCollection<ProfileListItem> Profiles { get; } = [];

    /// <summary>The subset of <see cref="Profiles"/> shown in the list: those whose name matches
    /// <see cref="SearchText"/>, plus the currently selected profile (so filtering never drops the
    /// active selection out from under the editor).</summary>
    public ObservableCollection<ProfileListItem> FilteredProfiles { get; } = [];

    /// <summary>Asked before honoring a selection change; false (dirty editor) reverts it.</summary>
    public Func<bool>? CanNavigate { get; set; }

    /// <summary>Raised after a selection change was allowed to stand.</summary>
    public Action<ProfileListItem?>? SelectionCommitted { get; set; }

    /// <summary>Raised when a selection change was blocked by CanNavigate.</summary>
    public Action? NavigationBlocked { get; set; }

    [ObservableProperty]
    public partial ProfileListItem? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial ProfileListItem? PendingDelete { get; set; }

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
        PendingDelete = null;
        SelectionCommitted?.Invoke(newValue);

        // Refresh the filtered view so a previously force-included (selected-but-unmatched) row
        // drops out now that the selection has moved on. Guarded so the transient clear/re-add
        // does not re-enter this handler as a real navigation.
        _revertingSelection = true;
        RebuildFiltered(newValue?.ProfileId);
        SelectedProfile = newValue;
        _revertingSelection = false;
    }

    partial void OnSearchTextChanged(string? value)
    {
        // Debounce: coalesce rapid keystrokes into a single filter pass. Cancelling the previous
        // token abandons the pending delay; the continuation resumes on the UI thread (Avalonia's
        // SynchronizationContext) so touching the observable collections stays thread-safe.
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        _ = ApplySearchAfterDelayAsync(_searchDebounceCts.Token);
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

        Guid? keepId = SelectedProfile?.ProfileId;
        _revertingSelection = true;
        RebuildFiltered(keepId);
        // The clear/re-add above can null the list's selection; re-assert it (same instance, now
        // present in FilteredProfiles) so the editor stays on the open profile while filtering.
        SelectedProfile = keepId is Guid id ? Profiles.FirstOrDefault(p => p.ProfileId == id) : null;
        _revertingSelection = false;
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
                Profiles.Add(new ProfileListItem(summary.ProfileId, summary.Name, summary.Active, summary.TriggerSummary));
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

    [RelayCommand]
    public void RequestDelete(ProfileListItem? item) => PendingDelete = item ?? SelectedProfile;

    [RelayCommand]
    public void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    public async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } doomed)
            return;
        try
        {
            var deleted = await gateway.DeleteProfileAsync(doomed.ProfileId);
            PendingDelete = null;
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
