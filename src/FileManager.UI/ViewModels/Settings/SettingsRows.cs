using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Contracts.Settings;
using System.Collections.Generic;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>One drive-type per-drive scan-thread override row (Settings → Per-drive overrides). Auto
/// defers to the per-drive default; otherwise the explicit worker count applies to every volume of this
/// class.</summary>
public sealed partial class DriveTypeOverrideRowViewModel : ViewModelBase
{
    public static IReadOnlyList<DriveClass> Options { get; } =
        [DriveClass.Fixed, DriveClass.Network, DriveClass.Removable, DriveClass.Optical, DriveClass.Ram, DriveClass.Unknown];

    [ObservableProperty] public partial DriveClass Class { get; set; } = DriveClass.Fixed;
    [ObservableProperty] public partial bool Auto { get; set; } = true;
    [ObservableProperty] public partial int Value { get; set; } = 1;

    public bool ShowValue => !Auto;

    partial void OnAutoChanged(bool value) => OnPropertyChanged(nameof(ShowValue));
}

/// <summary>One specific-drive per-drive scan-thread override row (Settings → Per-drive overrides),
/// keyed by a volume key ("c:", "\\server\share"). Highest precedence when its key matches a scanned
/// volume.</summary>
public sealed partial class SpecificDriveOverrideRowViewModel : ViewModelBase
{
    [ObservableProperty] public partial string VolumeKey { get; set; } = "";
    [ObservableProperty] public partial bool Auto { get; set; } = true;
    [ObservableProperty] public partial int Value { get; set; } = 1;

    public bool ShowValue => !Auto;

    partial void OnAutoChanged(bool value) => OnPropertyChanged(nameof(ShowValue));
}
