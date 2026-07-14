using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileManager.Core.Files;
using FileManager.Contracts.Primitives;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Files;

public class FileSystemServiceTests
{
    private static FileSystemService NewService() =>
        new(NullLogger<FileSystemService>.Instance);

    private static (FileSystemEntry? entry, EnumerationFault? fault) Split(
        Result<FileSystemEntry, EnumerationFault> item) =>
        item.Match<(FileSystemEntry?, EnumerationFault?)>(e => (e, null), f => (null, f));

    [Fact]
    public void EnumerateEntries_ListsDirectoriesAndFiles_AsSuccesses()
    {
        string root = Directory.CreateTempSubdirectory("fm-enum-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "file.txt"), "hi");

            var items = NewService().EnumerateEntries(root).ToList();

            Assert.All(items, i => Assert.True(i.IsSuccess));
            var entries = items.Select(i => Split(i).entry!).ToList();

            var dir = Assert.Single(entries, e => e.FileName == "sub");
            Assert.True(dir.IsDirectory);

            var file = Assert.Single(entries, e => e.FileName == "file.txt");
            Assert.False(file.IsDirectory);
            Assert.Equal(2, file.Size);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateEntries_MissingDirectory_YieldsSingleFatal()
    {
        string missing = Path.Combine(Path.GetTempPath(), "fm-does-not-exist-" + Guid.NewGuid().ToString("N"));

        var items = NewService().EnumerateEntries(missing).ToList();

        var item = Assert.Single(items);
        var (_, fault) = Split(item);
        Assert.NotNull(fault);
        Assert.Equal(EnumerationSeverity.Fatal, fault!.Value.Severity);
        Assert.Equal("Directory does not exist.", fault.Value.Message);
    }

    [Fact]
    public void EnumerateEntries_FileAtThePath_YieldsSingleFatal()
    {
        // A file where a directory is expected cannot be opened for enumeration — same
        // single-Fatal contract as a missing directory.
        string root = Directory.CreateTempSubdirectory("fm-enum-").FullName;
        try
        {
            string file = Path.Combine(root, "not-a-dir.txt");
            File.WriteAllText(file, "x");

            var items = NewService().EnumerateEntries(file).ToList();

            var item = Assert.Single(items);
            var (_, fault) = Split(item);
            Assert.NotNull(fault);
            Assert.Equal(EnumerationSeverity.Fatal, fault!.Value.Severity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Characterizes the exact timestamp/attribute contract downstream code depends on:
    /// a file's <see cref="FileSystemEntry.Modified"/> carries the LOCAL offset (the scanner calls
    /// <c>ToUniversalTime()</c> on it), <see cref="FileSystemEntry.Created"/> is UTC (offset zero),
    /// and <see cref="FileSystemEntry.Attributes"/> mirrors the stat. Any reimplementation must
    /// reproduce these values byte-for-byte.</summary>
    [Fact]
    public void EnumerateEntries_FileEntry_CarriesLocalModified_UtcCreated_AndAttributes()
    {
        string root = Directory.CreateTempSubdirectory("fm-enum-").FullName;
        try
        {
            string path = Path.Combine(root, "stamped.txt");
            File.WriteAllText(path, "12345");

            var entries = NewService().EnumerateEntries(root).Select(i => Split(i).entry!).ToList();
            FileSystemEntry file = Assert.Single(entries, e => e.FileName == "stamped.txt");

            Assert.Equal(path, file.FullPath);
            Assert.Equal(5, file.Size);

            DateTimeOffset lastWriteUtc = File.GetLastWriteTimeUtc(path);
            Assert.Equal(lastWriteUtc, file.Modified.ToUniversalTime());
            Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(lastWriteUtc), file.Modified.Offset);

            Assert.Equal(File.GetCreationTimeUtc(path), file.Created.UtcDateTime);
            Assert.Equal(TimeSpan.Zero, file.Created.Offset);

            Assert.Equal(File.GetAttributes(path), file.Attributes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Characterizes the directory quirk: only Modified is filled (local offset);
    /// Created stays default and Attributes stays 0. Downstream attribute checks rely on
    /// directories NOT carrying attributes, so a reimplementation must not silently enrich.</summary>
    [Fact]
    public void EnumerateEntries_DirectoryEntry_HasOnlyModified()
    {
        string root = Directory.CreateTempSubdirectory("fm-enum-").FullName;
        try
        {
            string sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);

            var entries = NewService().EnumerateEntries(root).Select(i => Split(i).entry!).ToList();
            FileSystemEntry dir = Assert.Single(entries, e => e.FileName == "sub");

            Assert.True(dir.IsDirectory);
            Assert.Equal(sub, dir.FullPath);
            Assert.Equal(0, dir.Size);
            Assert.Equal(Directory.GetLastWriteTimeUtc(sub), dir.Modified.ToUniversalTime().UtcDateTime);
            Assert.Equal(default, dir.Created);
            Assert.Equal((FileAttributes)0, dir.Attributes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateEntries_IncludesHiddenAndSystemFiles()
    {
        // The scanner filters on IsHidden/IsSystem METADATA — enumeration itself must not skip
        // them (EnumerationOptions defaults do; the compatible behavior does not).
        string root = Directory.CreateTempSubdirectory("fm-enum-").FullName;
        try
        {
            string hidden = Path.Combine(root, "hidden.txt");
            File.WriteAllText(hidden, "x");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var entries = NewService().EnumerateEntries(root).Select(i => Split(i).entry!).ToList();

            FileSystemEntry entry = Assert.Single(entries, e => e.FileName == "hidden.txt");
            Assert.True((entry.Attributes & FileAttributes.Hidden) != 0);
        }
        finally
        {
            File.SetAttributes(Path.Combine(root, "hidden.txt"), FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateEntries_IsLazy_NoWorkUntilIterated()
    {
        string missing = Path.Combine(Path.GetTempPath(), "fm-does-not-exist-" + Guid.NewGuid().ToString("N"));

        // Obtaining the sequence must not enumerate or throw; the fault only surfaces on iteration.
        IEnumerable<Result<FileSystemEntry, EnumerationFault>> sequence = NewService().EnumerateEntries(missing);

        using var e = sequence.GetEnumerator();
        Assert.True(e.MoveNext());
        Assert.False(e.Current.IsSuccess);
    }

    [Fact]
    public void MapWin32Error_IsAlwaysFatal_WithAnOsMessage()
    {
        // Any Win32 error that ends a single-level walk (failed open, failed advance) is terminal.
        var fault = FileSystemService.MapWin32Error(5);   // ERROR_ACCESS_DENIED

        Assert.Equal(EnumerationSeverity.Fatal, fault.Severity);
        Assert.False(string.IsNullOrWhiteSpace(fault.Message));
    }

    [Fact]
    public void EnumerateRoots_YieldsHomeAndReadyDrives_AsDirectories()
    {
        var service = NewService();

        var roots = service.EnumerateRoots()
            .Where(i => i.IsSuccess)
            .Select(i => Split(i).entry!)
            .ToList();

        Assert.NotEmpty(roots);
        Assert.All(roots, r => Assert.True(r.IsDirectory));

        string home = service.GetHomeDirectory().Match(p => p, _ => string.Empty);
        if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
            Assert.Contains(roots, r => r.FileName == "Home" && r.FullPath == home);
    }
}
