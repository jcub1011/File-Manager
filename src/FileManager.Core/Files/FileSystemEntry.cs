using System;

namespace FileManager.Core.Files;

/// <summary>
/// An immutable snapshot of a file system entry, captured at scan/ingestion time.
/// </summary>
/// <param name="FileName">The name of the file (including extension).</param>
/// <param name="FullPath">The absolute path of the entry.</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
/// <param name="Size">The size of the file in bytes.</param>
/// <param name="Modified">The time the entry was last modified.</param>
public sealed record FileSystemEntry(
    string FileName,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset Modified);
