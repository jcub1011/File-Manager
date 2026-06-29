using FileManager.Core.Primitives;
using System.Collections.Generic;

namespace FileManager.Core.Files;

/// <param name="Entries">The successfully retrieved entries.</param>
/// <param name="Warnings">The entries that failed retrieval.</param>
public readonly record struct FileSystemResults(IReadOnlyList<FileSystemEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>
/// Abstraction over the file system. Implementations must be platform-neutral so the
/// same build runs unchanged on Windows and Linux.
/// </summary>
public interface IFileSystemService
{
    /// <summary>
    /// Lists the directories and files directly under <paramref name="path"/>.
    /// Returns an empty list (never throws) when the path is unreadable or missing.
    /// </summary>
    public Result<FileSystemResults, string> GetEntries(string path);

    /// <summary>
    /// The root locations to offer as starting points: drive roots
    /// (<c>C:\</c>, <c>D:\</c> on Windows; <c>/</c> and mounts on Linux) plus the
    /// current user's home directory.
    /// </summary>
    public Result<FileSystemResults, string> GetRoots();

    /// <summary>The user's home / profile directory.</summary>
    public Result<string, string> GetHomeDirectory();

    /// <summary>
    /// The parent of <paramref name="path"/>, or <c>null</c> if it is already a root.
    /// </summary>
    public Result<string?, string> GetParent(string path);
}
