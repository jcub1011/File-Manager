using FileManager.Contracts.Primitives;
using System;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>Stat-level metadata reader. Exists because <see cref="FileSystemEntry"/> carries
/// only the enumeration snapshot (no created/hidden/system/symlink); the filter input and the
/// future job planner both need the full <see cref="FileMetadata"/>.</summary>
internal static class FileMetadataReader
{
    /// <summary>A <c>null</c> success value means the file does not exist (mirroring
    /// <see cref="File.Exists"/>, including "a directory sits at that path"); a failure means the
    /// path could not be stat'd at all — callers that knew the file existed should treat that as
    /// exists-but-unreadable, not as absent.</summary>
    public static Result<FileMetadata?, string> Read(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
                return Result<FileMetadata?, string>.Success(null);
            return new FileMetadata
            {
                Length = info.Length,
                LastWritten = info.LastWriteTimeUtc,
                Created = info.CreationTimeUtc,
                IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
                IsSystem = (info.Attributes & FileAttributes.System) != 0,
                IsSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not stat {path}: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return $"could not stat {path}: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
