using FileManager.Core.Files;

namespace FileManager.Core.Tests.Files;

public sealed class FileMetadataReaderTests : IDisposable
{
    private readonly string _dir;

    public FileMetadataReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Existing_file_returns_its_metadata()
    {
        string path = Path.Combine(_dir, "present.txt");
        File.WriteAllText(path, "12345");

        var result = FileMetadataReader.Read(path);

        Assert.True(result.TryGetValue(out var metadata));
        Assert.NotNull(metadata);
        Assert.Equal(5, metadata.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), metadata.LastWritten);
    }

    [Fact]
    public void Missing_file_is_a_null_success_not_a_failure()
    {
        var result = FileMetadataReader.Read(Path.Combine(_dir, "absent.txt"));

        Assert.True(result.TryGetValue(out var metadata));
        Assert.Null(metadata);
    }

    [Fact]
    public void Directory_at_the_path_reads_as_not_found()
    {
        // Mirrors File.Exists: a directory occupying the path is "no such file".
        var result = FileMetadataReader.Read(_dir);

        Assert.True(result.TryGetValue(out var metadata));
        Assert.Null(metadata);
    }

    [Fact]
    public void Unstatable_path_is_a_failure_not_not_found()
    {
        // An embedded NUL makes the path un-stat-able (FileInfo throws), which must surface as
        // a failure — distinct from the null-success "definitively absent".
        var result = FileMetadataReader.Read("bad\0path");

        Assert.True(result.TryGetError(out string? error));
        Assert.Contains("could not stat", error);
    }
}
