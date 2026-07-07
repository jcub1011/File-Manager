using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>One row of the profile list. Rows are replaced wholesale on refresh.</summary>
public sealed record ProfileListItem(Guid ProfileId, string Name, bool Active, string TriggerSummary)
{
    public string ActiveText => Active ? "Active" : "Inactive";
}

public sealed partial class ProfileListViewModel(IIpcGateway gateway) : ViewModelBase
{
    private bool _revertingSelection;

    public ObservableCollection<ProfileListItem> Profiles { get; } = [];

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
    }

    [RelayCommand]
    public async Task RefreshAsync()
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
        SelectedProfile = Profiles.FirstOrDefault(p => p.ProfileId == selectedId);
        _revertingSelection = false;
    }

    /// <summary>Refresh and select a profile by id (after a save), without re-firing navigation.</summary>
    public async Task RefreshAndSelectAsync(Guid profileId)
    {
        await RefreshAsync();
        _revertingSelection = true;
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
}
