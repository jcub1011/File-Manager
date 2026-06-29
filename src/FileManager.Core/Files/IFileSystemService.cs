using FileManager.Core.Primitives;
using System.Collections.Generic;

namespace FileManager.Core.Files;

/// <summary>How severe an <see cref="EnumerationFault"/> is.</summary>
public enum EnumerationSeverity
{
    /// <summary>A single entry failed; enumeration continues with the remaining entries.</summary>
    Warning,

    /// <summary>Enumeration could not continue; this is the terminal item of the sequence.</summary>
    Fatal,
}

/// <summary>A problem encountered while enumerating the file system.</summary>
/// <param name="Message">A human-readable description of the failure.</param>
/// <param name="Severity">Whether the stream continued (<see cref="EnumerationSeverity.Warning"/>) or stopped (<see cref="EnumerationSeverity.Fatal"/>).</param>
public readonly record struct EnumerationFault(string Message, EnumerationSeverity Severity);

/// <summary>
/// Abstraction over the file system. Implementations must be platform-neutral so the
/// same build runs unchanged on Windows and Linux.
/// </summary>
public interface IFileSystemService
{
    /// <summary>
    /// Lazily lists the directories and files directly under <paramref name="path"/>. Each item is
    /// either a successfully read <see cref="FileSystemEntry"/> or an <see cref="EnumerationFault"/>.
    /// A <see cref="EnumerationSeverity.Warning"/> fault (a single unreadable entry) interleaves with
    /// successes and enumeration continues; a <see cref="EnumerationSeverity.Fatal"/> fault (e.g. the
    /// path is missing or the directory cannot be opened) is always the last item in the sequence.
    /// Never throws — failures surface as faults.
    /// </summary>
    public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateEntries(string path);

    /// <summary>
    /// Lazily lists the root locations to offer as starting points: the current user's home
    /// directory plus the ready drive roots (<c>C:\</c>, <c>D:\</c> on Windows; <c>/</c> and mounts
    /// on Linux). Faults follow the same <see cref="EnumerationSeverity"/> convention as
    /// <see cref="EnumerateEntries"/>.
    /// </summary>
    public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateRoots();

    /// <summary>The user's home / profile directory.</summary>
    public Result<string, string> GetHomeDirectory();

    /// <summary>
    /// The parent of <paramref name="path"/>, or <c>null</c> if it is already a root.
    /// </summary>
    public Result<string?, string> GetParent(string path);
}
