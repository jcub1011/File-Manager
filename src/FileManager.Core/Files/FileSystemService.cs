using FileManager.Contracts.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>
/// <see cref="System.IO"/>-based implementation. Reflection-free and platform-neutral:
/// all path handling goes through <see cref="Path"/> and drive enumeration through
/// <see cref="DriveInfo"/>, so it behaves correctly on both Windows and Linux. Keep it
/// that way — this type is part of the AOT-clean surface.
/// </summary>
public sealed class FileSystemService(ILogger<FileSystemService> logger) : IFileSystemService
{
    public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateEntries(string path)
    {
        // `yield return` is not allowed inside a try/catch, so the enumerator is driven manually:
        // each MoveNext is guarded and the result is yielded outside the catch.
        IEnumerator<FileSystemInfo> enumerator = null!;
        EnumerationFault? setupFault = null;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                setupFault = new EnumerationFault("Directory does not exist.", EnumerationSeverity.Fatal);
            else
                enumerator = dir.EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retrieving entries failed.");
            setupFault = new EnumerationFault(ex.Message, EnumerationSeverity.Fatal);
        }

        if (setupFault is { } sf)
        {
            yield return sf;
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                FileSystemInfo current;
                EnumerationFault? fault = null;
                try
                {
                    if (!enumerator.MoveNext())
                        break;
                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A throwing enumerator is spent, so this fault is terminal.
                    logger.LogWarning(ex, "Error occurred before all entries could be read.");
                    fault = new EnumerationFault(ex.Message, EnumerationSeverity.Fatal);
                    current = null!;
                }

                if (fault is { } f)
                {
                    yield return f;
                    yield break;
                }

                Result<FileSystemEntry, EnumerationFault>? mapped = null;
                try
                {
                    mapped = current is DirectoryInfo
                        ? new FileSystemEntry(current.Name, current.FullName, true, 0, current.LastWriteTime)
                        : new FileSystemEntry(current.Name, current.FullName, false, ((FileInfo)current).Length, current.LastWriteTime);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Unable to read entry.");
                    mapped = new EnumerationFault(ex.Message, EnumerationSeverity.Warning);
                }

                yield return mapped.Value;
            }
        }
    }

    public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateRoots()
    {
        string home = GetHomeDirectory().Match(path => path, error => string.Empty);

        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            yield return new FileSystemEntry("Home", home, true, 0, SafeModified(home, logger));

        DriveInfo[] drives = [];
        EnumerationFault? drivesFault = null;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to read drives.");
            drivesFault = new EnumerationFault(ex.Message, EnumerationSeverity.Fatal);
        }

        if (drivesFault is { } df)
        {
            yield return df;
            yield break;
        }

        foreach (var drive in drives)
        {
            Result<FileSystemEntry, EnumerationFault>? mapped = null;
            try
            {
                if (!drive.IsReady)
                    continue;

                // On Windows this is "C:\", "D:\"; on Linux the single ready root is "/".
                string rootPath = drive.RootDirectory.FullName;
                mapped = new FileSystemEntry(rootPath, rootPath, true, 0, SafeModified(rootPath, logger));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to read drive.");
                mapped = new EnumerationFault(ex.Message, EnumerationSeverity.Warning);
            }

            if (mapped is { } m)
                yield return m;
        }
    }

    public Result<string, string> GetHomeDirectory()
    {
        try
        {
            string? path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(path)) return Result<string, string>.Failure("Unable to find home directory.");
            return Result<string, string>.Success(path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting home directory.");
            return Result<string, string>.Failure(ex.Message);
        }
    }

    public Result<string?, string> GetParent(string path)
    {
        try
        {
            return Result<string?, string>.Success(Directory.GetParent(path)?.FullName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get parent directory.");
            return Result<string?, string>.Failure(ex.Message);
        }
    }

    private static DateTime SafeModified(string path, ILogger logger)
    {
        try
        {
            return Directory.GetLastWriteTime(path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting last modified time.");
            return DateTime.MinValue;
        }
    }
}
