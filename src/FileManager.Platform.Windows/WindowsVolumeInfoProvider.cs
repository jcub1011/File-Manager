using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FileManager.Platform.Windows;

/// <summary>Windows volume queries for disk preflight (§4.3) and network-path detection (§4.2).
/// Uses Win32 free-space/drive-type calls (via source-generated <see cref="LibraryImportAttribute"/>)
/// so both drive-letter and UNC paths work — <see cref="DriveInfo"/> throws on UNC.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsVolumeInfoProvider(ILogger<WindowsVolumeInfoProvider> logger) : IVolumeInfoProvider
{
    private const uint DriveRemovable = 2;   // DRIVE_REMOVABLE
    private const uint DriveFixed = 3;       // DRIVE_FIXED
    private const uint DriveRemote = 4;      // DRIVE_REMOTE
    private const uint DriveCdRom = 5;       // DRIVE_CDROM
    private const uint DriveRamDisk = 6;     // DRIVE_RAMDISK

    public Result<long, string> GetAvailableFreeBytes(string path)
    {
        try
        {
            string dir = DirectoryOf(path);
            if (GetDiskFreeSpaceExW(dir, out ulong freeToCaller, out _, out _))
                return (long)Math.Min(freeToCaller, long.MaxValue);
            int err = Marshal.GetLastPInvokeError();
            return $"could not query free space for \"{path}\" (win32 {err})";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Free-space query failed for {Path}", path);
            return $"could not query free space for \"{path}\": {ex.Message}";
        }
    }

    /// <summary>Stable key grouping paths that share a volume: the drive root ("C:\") for local
    /// paths or the share root ("\\server\share") for UNC, lower-cased.</summary>
    public Result<string, string> GetVolumeKey(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return Result<string, string>.Failure($"could not determine the volume root of \"{path}\"");
            return Result<string, string>.Success(Path.TrimEndingDirectorySeparator(root).ToLowerInvariant());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Volume-key resolution failed for {Path}", path);
            return Result<string, string>.Failure($"could not determine the volume root of \"{path}\": {ex.Message}");
        }
    }

    public Result<VolumeCapacity, string> GetVolumeCapacity(string path)
    {
        try
        {
            string dir = DirectoryOf(path);
            if (!GetDiskFreeSpaceExW(dir, out _, out ulong totalBytes, out ulong totalFree))
            {
                int err = Marshal.GetLastPInvokeError();
                return Result<VolumeCapacity, string>.Failure(
                    $"could not query capacity for \"{path}\" (win32 {err})");
            }

            // Cluster size is a best-effort refinement of the footprint math — some volumes (certain
            // UNC shares) reject GetDiskFreeSpace. Fall back to 1 (no rounding) rather than failing
            // the whole projection.
            long cluster = 1;
            if (GetDiskFreeSpaceW(RootOf(dir), out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            {
                long candidate = (long)sectorsPerCluster * bytesPerSector;
                if (candidate > 0)
                    cluster = candidate;
            }

            return new VolumeCapacity(
                (long)Math.Min(totalBytes, long.MaxValue),
                (long)Math.Min(totalFree, long.MaxValue),
                cluster);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Capacity query failed for {Path}", path);
            return Result<VolumeCapacity, string>.Failure($"could not query capacity for \"{path}\": {ex.Message}");
        }
    }

    public bool IsNetworkPath(string path) => GetDriveClass(path) == DriveClass.Network;

    /// <summary>Maps the Win32 drive type to a <see cref="DriveClass"/>. A UNC share root is Network by
    /// definition; otherwise the drive-letter root's GetDriveType code is mapped. Any failure yields
    /// <see cref="DriveClass.Unknown"/> (the caller falls back to the per-drive default budget).</summary>
    public DriveClass GetDriveClass(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return DriveClass.Unknown;
            // UNC share root is a network path by definition (GetDriveType does not classify UNC).
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return DriveClass.Network;
            return GetDriveTypeW(root) switch
            {
                DriveRemovable => DriveClass.Removable,
                DriveFixed => DriveClass.Fixed,
                DriveRemote => DriveClass.Network,
                DriveCdRom => DriveClass.Optical,
                DriveRamDisk => DriveClass.Ram,
                _ => DriveClass.Unknown,
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Drive-class probe failed for {Path}; treating as unknown", path);
            return DriveClass.Unknown;
        }
    }

    private static string DirectoryOf(string path)
    {
        string full = Path.GetFullPath(path);
        // GetDiskFreeSpaceEx wants an EXISTING directory. A job's workspace/target directory may not
        // exist yet — disk preflight (§4.3) runs before the workspace is created — so walk up to the
        // nearest existing ancestor. Free space is a per-volume answer, so any existing ancestor on
        // the same volume gives the right number; bottom out at the volume root.
        string? dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(dir))
            dir = Path.GetPathRoot(full) ?? full;
        return ExtendIfLong(dir);
    }

    /// <summary>Prefixes an extended-length marker for paths near MAX_PATH: without it, deep trees
    /// fail the free-space preflight on default Windows (LongPathsEnabled off) even though the
    /// volume would answer. Kept per-directory (not hoisted to the root) because free space is a
    /// per-directory answer under quotas and mount points.</summary>
    private static string ExtendIfLong(string dir)
    {
        if (dir.Length < 248 || dir.StartsWith(@"\\?\", StringComparison.Ordinal))
            return dir;
        return dir.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(@"\\?\UNC\", dir.AsSpan(2))
            : @"\\?\" + dir;
    }

    /// <summary>The volume root GetDiskFreeSpaceW wants (drive root "C:\" or UNC share root with a
    /// trailing separator). Falls back to the directory itself when a root can't be derived.</summary>
    private static string RootOf(string directory)
    {
        string? root = Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(root))
            return directory;
        return root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(
        string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string lpRootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceW(
        string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
}
