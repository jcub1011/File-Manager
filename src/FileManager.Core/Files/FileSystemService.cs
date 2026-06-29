using FileManager.Core.Primitives;
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
    public Result<FileSystemResults, string> GetEntries(string path)
    {
        try
        {
            DirectoryInfo dir;
            dir = new DirectoryInfo(path);
            if (!dir.Exists) return "Directory does not exist.";

            var entries = new List<FileSystemEntry>();
            var failures = new List<string>();

            // Enumerate directories then files. Each access is guarded individually so a
            // single unreadable entry doesn't abort the whole listing.
            try
            {
                foreach (var sub in dir.EnumerateDirectories())
                {
                    try
                    {
                        entries.Add(new FileSystemEntry(sub.Name, sub.FullName, true, 0, sub.LastWriteTime));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Unable to read directory.");
                        failures.Add(ex.Message);
                    }
                }

                foreach (var file in dir.EnumerateFiles())
                {
                    try
                    {
                        entries.Add(new FileSystemEntry(file.Name, file.FullName, false, file.Length, file.LastWriteTime));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Unable to read file.");
                        failures.Add(ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Error occurred before all entries could be read.");
                failures.Add(ex.Message);
            }

            return new FileSystemResults(entries, failures);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retrieving entries failed.");
            return ex.Message;
        }
    }

    public Result<FileSystemResults, string> GetRoots()
    {
        var roots = new List<FileSystemEntry>();
        var failedRoots = new List<string>();

        string home = GetHomeDirectory().Match(path => path, error => string.Empty);

        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            roots.Add(new FileSystemEntry("Home", home, true, 0, SafeModified(home, logger)));

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                // On Windows this is "C:\", "D:\"; on Linux the single ready root is "/".
                string rootPath = drive.RootDirectory.FullName;
                roots.Add(new FileSystemEntry(rootPath, rootPath, true, 0, SafeModified(rootPath, logger)));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to read root directory.");
            failedRoots.Add(ex.Message);
        }

        return new FileSystemResults(roots, failedRoots);
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
