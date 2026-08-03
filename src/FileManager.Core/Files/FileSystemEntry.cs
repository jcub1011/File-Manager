using System;
using System.IO;

namespace FileManager.Core.Files;

/// <summary>
/// An immutable snapshot of a file system entry, captured at scan/ingestion time.
///
/// <para>Two construction paths, one observable shape. <see cref="InDirectory"/> stores the
/// containing directory (one shared string per enumerated directory) and joins
/// <see cref="FullPath"/> lazily on first read — the enumeration hot path never pays a per-entry
/// path string, which at a 500k-file destination sweep is ~100 MB of churn nobody reads (the sweep
/// works from the (directory, name) pair). The full-path constructor keeps serving synthetic
/// entries (drive roots, "Home", tests). Equality and hash cover the same seven members the
/// original record compared, so the two construction paths compare equal when they describe the
/// same entry — at the cost of materializing <see cref="FullPath"/> when actually compared.</para>
/// </summary>
public sealed class FileSystemEntry : IEquatable<FileSystemEntry>
{
    private readonly string? _directory;
    // Benign race by design: concurrent first reads may each join the path, one result wins —
    // both are equal strings, and a reference store is atomic. Never null once read.
    private string? _fullPath;

    /// <param name="fileName">The name of the file (including extension).</param>
    /// <param name="fullPath">The absolute path of the entry.</param>
    /// <param name="isDirectory">Whether this entry is a directory.</param>
    /// <param name="size">The size of the file in bytes.</param>
    /// <param name="modified">The time the entry was last modified.</param>
    /// <param name="created">The creation time (UTC), captured from the same enumeration snapshot.
    /// Default for entries (roots, home) where it is not read.</param>
    /// <param name="attributes">The file attributes captured from the enumeration snapshot — served
    /// from the cached directory scan, so reading them costs no extra stat. Lets the scanner build a
    /// full <see cref="FileMetadata"/> without re-stat'ing every file. Default (0) for entries where
    /// they are not read.</param>
    public FileSystemEntry(
        string fileName, string fullPath, bool isDirectory, long size, DateTimeOffset modified,
        DateTimeOffset created = default, FileAttributes attributes = 0)
        : this(fileName, directory: null, fullPath, isDirectory, size, modified, created, attributes)
    {
    }

    private FileSystemEntry(
        string fileName, string? directory, string? fullPath, bool isDirectory, long size,
        DateTimeOffset modified, DateTimeOffset created, FileAttributes attributes)
    {
        FileName = fileName;
        _directory = directory;
        _fullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
        Created = created;
        Attributes = attributes;
    }

    /// <summary>An entry described by its containing directory plus name — the enumeration shape.
    /// <see cref="FullPath"/> joins lazily; <paramref name="directory"/> is expected to be the same
    /// canonical string instance for every entry of one enumerated directory, which is what makes
    /// this construction allocation-free per entry.</summary>
    public static FileSystemEntry InDirectory(
        string directory, string fileName, bool isDirectory, long size, DateTimeOffset modified,
        DateTimeOffset created = default, FileAttributes attributes = 0) =>
        new(fileName, directory, fullPath: null, isDirectory, size, modified, created, attributes);

    /// <summary>The name of the file (including extension). For synthetic root entries this is a
    /// display name ("Home"), not necessarily the last path segment — which is why
    /// <see cref="FullPath"/> is never re-derived for full-path-constructed entries.</summary>
    public string FileName { get; }

    /// <summary>The absolute path of the entry. Materialized on first read for
    /// <see cref="InDirectory"/> entries — hot paths that can work from
    /// (<see cref="DirectoryHint"/>, <see cref="FileName"/>) should, so this never allocates.</summary>
    public string FullPath => _fullPath ??= Path.Join(_directory, FileName);

    /// <summary>The containing directory when this entry was built by <see cref="InDirectory"/>;
    /// null for full-path-constructed (synthetic) entries. Lets the destination sweep probe and
    /// convert without ever materializing <see cref="FullPath"/>.</summary>
    internal string? DirectoryHint => _directory;

    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTimeOffset Modified { get; }
    public DateTimeOffset Created { get; }
    public FileAttributes Attributes { get; }

    public bool Equals(FileSystemEntry? other) =>
        other is not null
        && FileName == other.FileName
        && FullPath == other.FullPath
        && IsDirectory == other.IsDirectory
        && Size == other.Size
        && Modified.Equals(other.Modified)
        && Created.Equals(other.Created)
        && Attributes == other.Attributes;

    public override bool Equals(object? obj) => Equals(obj as FileSystemEntry);

    public override int GetHashCode() =>
        HashCode.Combine(FileName, FullPath, IsDirectory, Size, Modified, Created, Attributes);

    public override string ToString() => FullPath;
}
