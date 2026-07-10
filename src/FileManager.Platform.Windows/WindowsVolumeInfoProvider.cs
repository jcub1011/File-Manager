using FileManager.Contracts.Primitives;
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
    private const uint DriveRemote = 4;   // DRIVE_REMOTE

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

    public bool IsNetworkPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return false;
            // UNC share root is a network path by definition.
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return true;
            // A mapped drive letter reports DRIVE_REMOTE.
            return GetDriveTypeW(root) == DriveRemote;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Network-path probe failed for {Path}; treating as local", path);
            return false;
        }
    }

    private static string DirectoryOf(string path)
    {
        string full = Path.GetFullPath(path);
        // GetDiskFreeSpaceEx wants a directory; if the path is (or looks like) a file, use its parent.
        if (Directory.Exists(full))
            return full;
        return Path.GetDirectoryName(full) ?? full;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(
        string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string lpRootPathName);
}
