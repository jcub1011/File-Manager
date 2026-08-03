using FileManager.Core.Files;

namespace FileManager.Core.Tests.Files;

/// <summary>The two <see cref="FileSystemEntry"/> construction paths — full path (synthetic entries)
/// and (directory, name) with a lazily joined <see cref="FileSystemEntry.FullPath"/> (the
/// enumeration hot path) — must be observationally identical: same FullPath string values, equal
/// under value equality, same hash. The lazy path exists so the destination sweep never pays a
/// per-entry path string; these pin that it costs no behavior.</summary>
public sealed class FileSystemEntryTests
{
    [Theory]
    [InlineData(@"C:\data\sub", "x.txt", @"C:\data\sub\x.txt")]
    // volume root: the directory keeps its separator, the join must not double it
    [InlineData(@"C:\", "x.txt", @"C:\x.txt")]
    // directory carrying a trailing separator joins cleanly too
    [InlineData(@"C:\data\", "x.txt", @"C:\data\x.txt")]
    [InlineData(@"\\server\share\team", "doc.bin", @"\\server\share\team\doc.bin")]
    public void InDirectory_joins_FullPath_like_the_full_path_constructor(
        string directory, string fileName, string expectedFullPath)
    {
        FileSystemEntry lazy = FileSystemEntry.InDirectory(directory, fileName, false, 1, DateTimeOffset.UnixEpoch);

        Assert.Equal(expectedFullPath, lazy.FullPath);
        // Reading twice returns the cached join (same instance, not just an equal string).
        Assert.Same(lazy.FullPath, lazy.FullPath);
    }

    [Fact]
    public void Construction_paths_are_value_equal_when_they_describe_the_same_entry()
    {
        FileSystemEntry lazy = FileSystemEntry.InDirectory(
            @"C:\data", "x.txt", false, 42, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, System.IO.FileAttributes.Archive);
        FileSystemEntry eager = new(
            "x.txt", @"C:\data\x.txt", false, 42, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, System.IO.FileAttributes.Archive);

        Assert.Equal(eager, lazy);
        Assert.Equal(eager.GetHashCode(), lazy.GetHashCode());
    }

    [Fact]
    public void Entries_differing_only_by_directory_are_not_equal()
    {
        FileSystemEntry a = FileSystemEntry.InDirectory(@"C:\one", "x.txt", false, 1, DateTimeOffset.UnixEpoch);
        FileSystemEntry b = FileSystemEntry.InDirectory(@"C:\two", "x.txt", false, 1, DateTimeOffset.UnixEpoch);

        Assert.NotEqual(a, b);
    }
}
