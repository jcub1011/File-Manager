using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;

namespace FileManager.Core.Platform;

public interface IVolumeInfoProvider
{
    Result<long, string> GetAvailableFreeBytes(string path);

    /// <summary>Stable key grouping paths that share a volume (drive root or UNC share root), in the
    /// canonical form <see cref="VolumeKeys.Normalize"/> defines — "c:", "\\server\share".</summary>
    Result<string, string> GetVolumeKey(string path);

    bool IsNetworkPath(string path);

    /// <summary>The physical medium behind the volume holding <paramref name="path"/>, used to size the
    /// per-drive scan-thread budget. Best-effort: returns <see cref="DriveClass.Unknown"/> when the
    /// class cannot be determined rather than failing.</summary>
    DriveClass GetDriveClass(string path);

    /// <summary>Total capacity, free space, and allocation-unit (cluster) size for the volume holding
    /// <paramref name="path"/>. Powers the dry-run space preview's "used / still free" and the
    /// cluster rounding of on-disk footprints. May fail on volumes that do not report capacity (some
    /// UNC shares); callers treat that as "capacity unknown" rather than a hard error.</summary>
    Result<VolumeCapacity, string> GetVolumeCapacity(string path);
}

/// <summary>A volume's total/free capacity plus its allocation-unit size. <see cref="BytesPerCluster"/>
/// is 1 when unknown (i.e. no rounding).</summary>
public readonly record struct VolumeCapacity(long TotalBytes, long FreeBytes, long BytesPerCluster);
