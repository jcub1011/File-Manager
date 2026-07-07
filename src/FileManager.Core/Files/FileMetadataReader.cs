using FileManager.Contracts.Primitives;
using System;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>Stat-level metadata reader. Exists because <see cref="FileSystemEntry"/> carries
/// only the enumeration snapshot (no created/hidden/system/symlink); the filter input and the
/// future job planner both need the full <see cref="FileMetadata"/>.</summary>
internal static class FileMetadataReader
{
    public static Result<FileMetadata, string> Read(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
                return $"file not found: {path}";
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
