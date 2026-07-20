namespace FileManager.Contracts.Settings;

/// <summary>The physical medium behind a volume, used to pick a per-drive scan-thread budget
/// (<see cref="ScanThreadingSettings.DriveTypeOverrides"/>). Mirrors the Win32 GetDriveType classes,
/// collapsing UNC/mapped-remote roots to <see cref="Network"/>.</summary>
public enum DriveClass
{
    [Tooltip("Unknown")]
    Unknown,
    [Tooltip("Fixed (HDD/SSD)")]
    Fixed,
    [Tooltip("Removable")]
    Removable,
    [Tooltip("Network")]
    Network,
    [Tooltip("Optical")]
    Optical,
    [Tooltip("RAM Disk")]
    Ram,
}
