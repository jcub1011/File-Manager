using System;

namespace FileManager.Core.Files;

/// <summary>
/// A file's stat level metadata.
/// </summary>
public sealed record FileMetadata
{
    /// <summary>The length in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Time of last modification.</summary>
    public required DateTimeOffset LastWritten { get; init; }

    /// <summary>Time of creation.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>Whether the file carries the Hidden attribute.</summary>
    public required bool IsHidden { get; init; }

    /// <summary>Whether the file carries the System attribute.</summary>
    public required bool IsSystem { get; init; }

    /// <summary>Whether the file is a reparse point (symbolic link / junction).</summary>
    public required bool IsSymlink { get; init; }
}
