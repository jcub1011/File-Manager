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
