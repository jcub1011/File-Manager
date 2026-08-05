using FileManager.Contracts.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using SysEntry = System.IO.Enumeration.FileSystemEntry;

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
        // each MoveNext is guarded and the result is yielded outside the catch. The enumerator is a
        // FileSystemEnumerator subclass reading straight from the OS find-data — zero allocation per
        // entry (no FileInfo/DirectoryInfo objects), which is the point of this shape.
        Enumerator enumerator = null!;
        EnumerationFault? setupFault = null;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                setupFault = new EnumerationFault("Directory does not exist.", EnumerationSeverity.Fatal);
            else
                // FullName, not the raw path: entries are built as (directory, name) pairs sharing
                // the ctor string, so it must be the same canonical form FileSystemEnumerator's own
                // normalization would have produced for ToFullPath (a relative or ..-laden path
                // would otherwise leak into every entry's lazily joined FullPath).
                enumerator = new Enumerator(dir.FullName, logger);
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
                Result<FileSystemEntry, EnumerationFault> mapped;
                EnumerationFault? fault = null;
                try
                {
                    if (!enumerator.MoveNext())
                        break;
                    mapped = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A throwing enumerator is spent, so this fault is terminal.
                    logger.LogWarning(ex, "Error occurred before all entries could be read.");
                    fault = new EnumerationFault(ex.Message, EnumerationSeverity.Fatal);
                    mapped = default;
                }
                catch (Exception ex)
                {
                    // Last resort: unexpected exceptions become logged faults, not faulted callers.
                    logger.LogError(ex, "Enumeration failed unexpectedly.");
                    fault = new EnumerationFault($"{ex.GetType().Name}: {ex.Message}", EnumerationSeverity.Fatal);
                    mapped = default;
                }

                if (fault is { } f)
                {
                    yield return f;
                    yield break;
                }

                yield return mapped;
            }

            // A Win32 error captured by ContinueOnError ended the walk early — a failed directory
            // open or a failed FindNext, both terminal for a single-level enumeration. Surface it
            // with the same Fatal contract as a throwing enumerator.
            if (enumerator.TakePendingFault() is { } pending)
                yield return pending;
        }
    }

    /// <summary>Single-level, allocation-free enumeration: the transform reads each entry from a
    /// <see cref="SysEntry"/> ref struct over the OS find-data. Callers do their own recursion, so
    /// subdirectories are yielded, not descended. Options mirror the old
    /// <c>DirectoryInfo.EnumerateFileSystemInfos()</c> behavior: hidden/system entries are included
    /// (the scanner filters on metadata, not at enumeration) and errors are never swallowed —
    /// <see cref="ContinueOnError"/> captures the Win32 code for the wrapper to yield as a fault.</summary>
    private sealed class Enumerator(string path, ILogger logger)
        : FileSystemEnumerator<Result<FileSystemEntry, EnumerationFault>>(path, CompatibleOptions)
    {
        private static readonly EnumerationOptions CompatibleOptions = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
        };

        private int _pendingError = -1;

        protected override bool ContinueOnError(int error)
        {
            // Never throw out of the walk: capture the Win32 error (first one wins) and let the
            // enumeration end; the wrapper turns it into a terminal Fatal fault.
            if (_pendingError < 0)
                _pendingError = error;
            return true;
        }

        public EnumerationFault? TakePendingFault()
        {
            if (_pendingError < 0)
                return null;
            EnumerationFault fault = MapWin32Error(_pendingError);
            logger.LogWarning("Error occurred before all entries could be read: {Message} (Win32 {Code})",
                fault.Message, _pendingError);
            _pendingError = -1;
            return fault;
        }

        protected override Result<FileSystemEntry, EnumerationFault> TransformEntry(ref SysEntry entry)
        {
            try
            {
                // All of Length/CreationTimeUtc/Attributes are served from the find-data snapshot
                // (no per-entry stat), so the scanner can build a full FileMetadata for free.
                // Contract (characterized in FileSystemServiceTests): Modified carries the LOCAL
                // offset, Created is UTC. Directories carry Modified AND Attributes — the
                // OnSubdirectory reparse-point guards (junction cycles / root escape) are dead code
                // without the attribute flags; only Created stays unenriched for them.
                //
                // InDirectory, not ToFullPath(): this is a single-level walk, so every entry's
                // containing directory IS the ctor path — one shared string per directory instead
                // of a joined path string per entry. FullPath joins lazily for the consumers that
                // read it; the destination sweep never does.
                return entry.IsDirectory
                    ? FileSystemEntry.InDirectory(path, entry.FileName.ToString(), true, 0,
                        entry.LastWriteTimeUtc.ToLocalTime(), default, entry.Attributes)
                    : FileSystemEntry.InDirectory(path, entry.FileName.ToString(), false, entry.Length,
                        entry.LastWriteTimeUtc.ToLocalTime(), entry.CreationTimeUtc, entry.Attributes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to read entry.");
                return new EnumerationFault(ex.Message, EnumerationSeverity.Warning);
            }
            catch (Exception ex)
            {
                // Last resort: unexpected exceptions become logged faults, not faulted callers.
                logger.LogError(ex, "Reading an entry failed unexpectedly.");
                return new EnumerationFault($"{ex.GetType().Name}: {ex.Message}", EnumerationSeverity.Warning);
            }
        }
    }

    /// <summary>Win32 error → fault mapping for enumeration errors. Every error that reaches this
    /// point ended a single-level walk (a failed open or a failed advance), so the severity is
    /// always Fatal; the message comes from the OS. Kept as a seam so the mapping is testable
    /// without forcing real filesystem errors.</summary>
    internal static EnumerationFault MapWin32Error(int error) =>
        new(new Win32Exception(error).Message, EnumerationSeverity.Fatal);

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
            catch (Exception ex)
            {
                // Last resort: unexpected exceptions become logged faults, not faulted callers.
                logger.LogError(ex, "Reading a drive failed unexpectedly.");
                mapped = new EnumerationFault($"{ex.GetType().Name}: {ex.Message}", EnumerationSeverity.Warning);
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
