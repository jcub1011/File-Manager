using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FileManager.UI.ViewModels;

/// <summary>Exports a multi-selection of profiles to a chosen folder, one <c>.json</c> per profile in
/// the on-disk format (so the files round-trip through Import). Loads the list over IPC, then for each
/// checked profile fetches the full <see cref="Profile"/> and writes it atomically.</summary>
public sealed partial class ExportProfilesViewModel : ViewModelBase
{
    private readonly IIpcGateway _gateway;
    private readonly IFolderPicker _folderPicker;

    public ExportProfilesViewModel(IIpcGateway gateway, IFolderPicker folderPicker)
    {
        _gateway = gateway;
        _folderPicker = folderPicker;
    }

    public ObservableCollection<ExportProfileRow> Profiles { get; } = [];

    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public bool HasProfiles => Profiles.Count > 0;
    public bool CanExport => Profiles.Any(p => p.IsSelected);

    /// <summary>Set by the host so a successful export can close the dialog. Kept as a callback so the
    /// VM stays window-agnostic (mirrors <see cref="SettingsViewModel.RequestClose"/>).</summary>
    public Action? RequestClose { get; set; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await _gateway.ListProfilesAsync();
            if (result.TryGetError(out IpcError? error))
            {
                ErrorMessage = $"Could not load profiles: {error.Message}";
                return;
            }
            result.TryGetValue(out IReadOnlyList<ProfileSummary>? summaries);

            Profiles.Clear();
            foreach (ProfileSummary summary in summaries!.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                ExportProfileRow row = new(summary.ProfileId, summary.Name);
                row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanExport));
                Profiles.Add(row);
            }
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(CanExport));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Loading profiles for export failed unexpectedly");
            ErrorMessage = $"Could not load profiles: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        bool selectAll = !Profiles.All(p => p.IsSelected);
        foreach (ExportProfileRow row in Profiles)
            row.IsSelected = selectAll;
    }

    [RelayCommand]
    private async Task Export()
    {
        ErrorMessage = null;
        StatusMessage = null;

        List<ExportProfileRow> selected = Profiles.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "Select at least one profile to export.";
            return;
        }

        string? folder = await _folderPicker.PickFolderAsync("Choose an export folder");
        if (folder is null)
            return;

        IsBusy = true;
        try
        {
            HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
            int exported = 0;
            List<string> failures = [];

            foreach (ExportProfileRow row in selected)
            {
                var loaded = await _gateway.GetProfileAsync(row.ProfileId);
                if (loaded.TryGetError(out IpcError? error))
                {
                    failures.Add($"{row.Name}: {error.Message}");
                    continue;
                }
                loaded.TryGetValue(out Profile? profile);

                try
                {
                    string fileName = ProfileExport.UniqueFileName(profile!.Name, folder, usedNames);
                    ProfileExport.WriteAtomic(Path.Combine(folder, fileName), profile);
                    exported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{row.Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Last-resort catch-all (directive): an unexpected per-profile failure (e.g. a
                    // reserved device name) records THIS profile as failed and the batch continues,
                    // instead of aborting every remaining selection via the outer catch.
                    Serilog.Log.Error(ex, "Exporting profile {ProfileId} failed unexpectedly", row.ProfileId);
                    failures.Add($"{row.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (failures.Count == 0)
            {
                RequestClose?.Invoke();   // clean export: the dialog closes; the files are the feedback
            }
            else
            {
                // Partial failure stays on the danger-styled bar (StatusMessage renders success-styled).
                ErrorMessage = $"Exported {exported} profile(s); {failures.Count} failed: {string.Join("; ", failures)}";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Exporting profiles failed unexpectedly");
            ErrorMessage = $"Export failed unexpectedly: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A checkbox row in the export dialog.</summary>
public sealed partial class ExportProfileRow(Guid profileId, string name) : ViewModelBase
{
    public Guid ProfileId { get; } = profileId;
    public string Name { get; } = name;

    [ObservableProperty] public partial bool IsSelected { get; set; }
}
