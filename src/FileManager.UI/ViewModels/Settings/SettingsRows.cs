using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using FileManager.UI.Undo;
using System.Collections.Generic;

namespace FileManager.UI.ViewModels.Settings;

/// <summary>One drive-type per-drive scan-thread override row (Settings → Per-drive overrides). Auto
/// defers to the per-drive default; otherwise the explicit worker count applies to every volume of this
/// class.</summary>
public sealed partial class DriveTypeOverrideRowViewModel : ViewModelBase, IUndoTrackable
{
    public static IReadOnlyList<DriveClass> Options { get; } =
        [DriveClass.Fixed, DriveClass.Network, DriveClass.Removable, DriveClass.Optical, DriveClass.Ram, DriveClass.Unknown];

    [ObservableProperty] public partial DriveClass Class { get; set; } = DriveClass.Fixed;
    [ObservableProperty] public partial bool Auto { get; set; } = true;
    [ObservableProperty] public partial int Value { get; set; } = 1;

    public bool ShowValue => !Auto;

    partial void OnAutoChanged(bool value) => OnPropertyChanged(nameof(ShowValue));

    public IEnumerable<UndoableProperty> UndoableProperties =>
    [
        UndoableProperty.For(nameof(Class), () => Class, v => Class = v),
        UndoableProperty.For(nameof(Auto), () => Auto, v => Auto = v),
        UndoableProperty.For(nameof(Value), () => Value, v => Value = v, coalesce: true),
    ];
}

/// <summary>One specific-drive per-drive scan-thread override row (Settings → Per-drive overrides),
/// keyed by a volume key ("c:", "\\server\share"). Highest precedence when its key matches a scanned
/// volume.
///
/// The key can be picked from <see cref="AvailableDrives"/> or typed. Both are offered because they
/// cover different cases: the picker removes any doubt about the exact key format for a mounted drive,
/// while the text box is the only way to name a UNC share or a drive that is not attached right now.</summary>
public sealed partial class SpecificDriveOverrideRowViewModel : ViewModelBase, IUndoTrackable
{
    /// <summary>Guards the two-way <see cref="VolumeKey"/> ↔ <see cref="SelectedDrive"/> sync so each
    /// side settles without re-triggering the other.</summary>
    private bool _syncing;

    /// <summary>The machine's mounted volumes, supplied by the owning setting. Empty when drives could
    /// not be enumerated, which simply leaves the picker empty and the text box in charge.</summary>
    public IReadOnlyList<DriveOption> AvailableDrives { get; init; } = [];

    [ObservableProperty] public partial string VolumeKey { get; set; } = "";
    [ObservableProperty] public partial bool Auto { get; set; } = true;
    [ObservableProperty] public partial int Value { get; set; } = 1;

    /// <summary>The picker's selection — a view of <see cref="VolumeKey"/>, not a second source of truth.
    /// Null whenever the key names something the picker cannot show: a UNC share, an unmounted drive, or
    /// a key still half-typed. That is why the combo carries placeholder text rather than looking broken
    /// when it reads empty.
    ///
    /// Deliberately NOT undo-tracked: only <see cref="VolumeKey"/> is, so picking a drive is exactly one
    /// undo step instead of two.</summary>
    [ObservableProperty] public partial DriveOption? SelectedDrive { get; set; }

    public bool ShowValue => !Auto;

    partial void OnAutoChanged(bool value) => OnPropertyChanged(nameof(ShowValue));

    partial void OnSelectedDriveChanged(DriveOption? value)
    {
        if (_syncing || value is null)
            return;
        _syncing = true;
        try
        {
            VolumeKey = value.VolumeKey;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnVolumeKeyChanged(string value)
    {
        if (_syncing)
            return;
        _syncing = true;
        try
        {
            SelectedDrive = MatchDrive(value);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The listed drive whose key equals this one after normalization, or null. Normalizing both
    /// sides means a typed "C:\" still selects the C: entry.</summary>
    private DriveOption? MatchDrive(string key)
    {
        string normalized = VolumeKeys.Normalize(key);
        if (normalized.Length == 0)
            return null;
        foreach (DriveOption drive in AvailableDrives)
        {
            if (drive.VolumeKey == normalized)
                return drive;
        }
        return null;
    }

    public IEnumerable<UndoableProperty> UndoableProperties =>
    [
        UndoableProperty.For(nameof(VolumeKey), () => VolumeKey, v => VolumeKey = v, coalesce: true),
        UndoableProperty.For(nameof(Auto), () => Auto, v => Auto = v),
        UndoableProperty.For(nameof(Value), () => Value, v => Value = v, coalesce: true),
    ];
}
