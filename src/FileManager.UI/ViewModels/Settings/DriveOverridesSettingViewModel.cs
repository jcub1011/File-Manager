using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>The per-drive scan-thread overrides — a setting whose editor is two editable lists rather
/// than a single control.
///
/// This is the worked example of the escape hatch for a setting the stock kinds cannot express: derive
/// from <see cref="SettingItemViewModel"/>, hold whatever state the editor needs, and add one
/// <c>DataTemplate</c> for the type in <c>SettingsWindow.axaml</c>. Search, the navigation tree node,
/// and the scroll anchor all come from the base class, so nothing else has to know about it.</summary>
public sealed partial class DriveOverridesSettingViewModel : SettingItemViewModel
{
    /// <summary>The number an added row starts at, supplied by the owner so it matches the engine's
    /// per-drive auto formula.</summary>
    private readonly int _defaultValue;

    public DriveOverridesSettingViewModel(string id, string title, string description, int defaultValue, string[]? keywords = null)
        : base(id, title, description, keywords)
        => _defaultValue = defaultValue;

    public ObservableCollection<DriveTypeOverrideRowViewModel> DriveTypeOverrides { get; } = [];
    public ObservableCollection<SpecificDriveOverrideRowViewModel> SpecificDriveOverrides { get; } = [];

    [RelayCommand] private void AddDriveTypeOverride() => DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Value = _defaultValue });
    [RelayCommand] private void RemoveDriveTypeOverride(DriveTypeOverrideRowViewModel row) => DriveTypeOverrides.Remove(row);
    [RelayCommand] private void AddSpecificDriveOverride() => SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { Value = _defaultValue });
    [RelayCommand] private void RemoveSpecificDriveOverride(SpecificDriveOverrideRowViewModel row) => SpecificDriveOverrides.Remove(row);
}
