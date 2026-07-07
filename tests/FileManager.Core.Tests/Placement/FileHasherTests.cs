using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Placement;

public sealed class FileHasherTests
{
    private static readonly FileHasher Hasher = new(NullLogger<FileHasher>.Instance);

    [Fact]
    public async Task Empty_file_produces_the_known_sha256_vector()
    {
        string path = Path.GetTempFileName();
        try
        {
            var hashed = await Hasher.HashFileAsync(path);
            Assert.True(hashed.TryGetValue(out string? hash));
            Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Hashes_a_file_already_open_for_writing()
    {
        string path = Path.GetTempFileName();
        try
        {
            await using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var hashed = await Hasher.HashFileAsync(path);
            Assert.True(hashed.IsSuccess);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_file_is_a_SourceUnreadable_error()
    {
        var hashed = await Hasher.HashFileAsync(Path.Combine(Path.GetTempPath(), "fm-no-such-" + Guid.NewGuid().ToString("N")));
        Assert.True(hashed.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.SourceUnreadable, error.Code);
    }
}
