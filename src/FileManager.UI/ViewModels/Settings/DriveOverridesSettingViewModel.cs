using CommunityToolkit.Mvvm.Input;
using FileManager.UI.Services;
using FileManager.UI.Undo;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>The per-drive scan-thread overrides — a setting whose editor is two editable lists rather
/// than a single control.
///
/// This is the worked example of the escape hatch for a setting the stock kinds cannot express: derive
/// from <see cref="SettingItemViewModel"/>, hold whatever state the editor needs, and add one
/// <c>DataTemplate</c> for the type in <c>SettingsWindow.axaml</c>. Search, the navigation tree node,
/// the scroll anchor, and (via <see cref="TrackNested"/>) undo/redo all come from the base machinery, so
/// nothing else has to know about it.</summary>
public sealed partial class DriveOverridesSettingViewModel : SettingItemViewModel, IUndoTrackable
{
    /// <summary>The number an added row starts at, supplied by the owner so it matches the engine's
    /// per-drive auto formula.</summary>
    private readonly int _defaultValue;

    /// <summary>The machine's mounted volumes, enumerated once and shared by every specific-drive row —
    /// the list cannot change while a modal settings dialog is open, and re-querying per row would hit
    /// the disk once per keystroke-free row for no gain.</summary>
    private readonly IReadOnlyList<DriveOption> _availableDrives;

    public DriveOverridesSettingViewModel(
        string id, string title, string description, int defaultValue,
        ISystemDrives? drives = null, string[]? keywords = null)
        : base(id, title, description, keywords)
    {
        _defaultValue = defaultValue;
        _availableDrives = drives?.List() ?? [];
    }

    public ObservableCollection<DriveTypeOverrideRowViewModel> DriveTypeOverrides { get; } = [];
    public ObservableCollection<SpecificDriveOverrideRowViewModel> SpecificDriveOverrides { get; } = [];

    [RelayCommand] private void AddDriveTypeOverride() => DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Value = _defaultValue });
    [RelayCommand] private void RemoveDriveTypeOverride(DriveTypeOverrideRowViewModel row) => DriveTypeOverrides.Remove(row);
    [RelayCommand] private void AddSpecificDriveOverride() => SpecificDriveOverrides.Add(NewSpecificRow());
    [RelayCommand] private void RemoveSpecificDriveOverride(SpecificDriveOverrideRowViewModel row) => SpecificDriveOverrides.Remove(row);

    /// <summary>A specific-drive row wired to the shared drive list. The only way rows should be built —
    /// a row constructed without it gets an empty picker.</summary>
    public SpecificDriveOverrideRowViewModel NewSpecificRow() =>
        new() { Value = _defaultValue, AvailableDrives = _availableDrives };

    /// <summary>This setting's state is its two lists, not scalar properties, so there is nothing for the
    /// property recorder to take.</summary>
    public IEnumerable<UndoableProperty> UndoableProperties => [];

    /// <summary>Adding, removing and editing rows all become undo steps; the row view models declare
    /// their own properties and the history picks them up as rows enter the lists.</summary>
    public void TrackNested(UndoHistory history)
    {
        history.TrackCollection(DriveTypeOverrides);
        history.TrackCollection(SpecificDriveOverrides);
    }
}
