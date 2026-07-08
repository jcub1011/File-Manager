using System;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>
/// An immutable snapshot of a file system entry, captured at scan/ingestion time.
/// </summary>
/// <param name="FileName">The name of the file (including extension).</param>
/// <param name="FullPath">The absolute path of the entry.</param>
/// <param name="IsDirectory">Whether this entry is a directory.</param>
/// <param name="Size">The size of the file in bytes.</param>
/// <param name="Modified">The time the entry was last modified.</param>
/// <param name="Created">The creation time (UTC), captured from the same enumeration snapshot.
/// Default for entries (roots, home) where it is not read.</param>
/// <param name="Attributes">The file attributes captured from the enumeration snapshot — served
/// from the cached directory scan, so reading them costs no extra stat. Lets the scanner build a
/// full <see cref="FileMetadata"/> without re-stat'ing every file. Default (0) for entries where
/// they are not read.</param>
public sealed record FileSystemEntry(
    string FileName,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset Modified,
    DateTimeOffset Created = default,
    FileAttributes Attributes = 0);
