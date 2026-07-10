using System.IO.Hashing;
using FileManager.Contracts.Profiles;
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
            var hashed = await Hasher.HashFileAsync(path, VerificationMethod.Sha256);
            Assert.True(hashed.TryGetValue(out string? hash));
            Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Empty_file_produces_the_XxHash128_reference_digest()
    {
        // Assert the streaming loop + hex encoding match the library's canonical one-shot, and that
        // the digest is the expected 128-bit width (32 hex chars) — without hand-typing a vector.
        string expected = Convert.ToHexString(XxHash128.Hash(ReadOnlySpan<byte>.Empty));
        string path = Path.GetTempFileName();
        try
        {
            var hashed = await Hasher.HashFileAsync(path, VerificationMethod.XxHash128);
            Assert.True(hashed.TryGetValue(out string? hash));
            Assert.Equal(32, hash!.Length);
            Assert.Equal(expected, hash);
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
            var hashed = await Hasher.HashFileAsync(path, VerificationMethod.XxHash128);
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
        var hashed = await Hasher.HashFileAsync(Path.Combine(Path.GetTempPath(), "fm-no-such-" + Guid.NewGuid().ToString("N")), VerificationMethod.XxHash128);
        Assert.True(hashed.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.SourceUnreadable, error.Code);
    }

    [Fact]
    public async Task Unexpected_exception_is_a_traceable_failure_not_a_throw()
    {
        // A null path throws ArgumentNullException — not one of the expected I/O exception
        // types — which the last-resort catch must convert to a failure carrying the type name.
        var hashed = await Hasher.HashFileAsync(null!, VerificationMethod.XxHash128);
        Assert.True(hashed.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.SourceUnreadable, error.Code);
        Assert.Contains(nameof(ArgumentNullException), error.Message);
    }

    [Fact]
    public async Task Cancellation_yields_a_Canceled_result_not_an_exception()
    {
        string path = Path.GetTempFileName();
        try
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            var hashed = await Hasher.HashFileAsync(path, VerificationMethod.XxHash128, cts.Token);
            Assert.True(hashed.IsCanceled);
            Assert.False(hashed.TryGetError(out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
