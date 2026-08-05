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

    // ---- sampled hashing (the bounded-read identity probe) --------------------------------------

    /// <summary>A layout small enough to keep the fixtures fast while still exercising real sampling:
    /// 4 KiB windows × 2 interior ⇒ a 16 KiB budget, so a 64 KiB file is genuinely sampled (four
    /// windows, most of the file unread).</summary>
    private static readonly SampledHashLayout SmallLayout =
        new() { WindowSizeBytes = 4096, InteriorWindowCount = 2 };

    private static string WriteFile(long length, int seed = 7)
    {
        string path = Path.Combine(Path.GetTempPath(), "fm-sampled-" + Guid.NewGuid().ToString("N"));
        byte[] block = new byte[4096];
        Random rng = new(seed);
        using FileStream fs = File.Create(path);
        for (long written = 0; written < length; written += block.Length)
        {
            rng.NextBytes(block);
            fs.Write(block, 0, (int)Math.Min(block.Length, length - written));
        }
        return path;
    }

    private static async Task<byte[]> Sampled(string path, SampledHashLayout layout)
    {
        var hashed = await Hasher.HashSampledToBytesAsync(path, layout);
        Assert.True(hashed.TryGetValue(out byte[]? digest), "sampled hash should succeed");
        return digest!;
    }

    [Fact]
    public async Task Sampled_digest_is_128_bits_and_stable_across_calls()
    {
        string path = WriteFile(64 * 1024);
        try
        {
            byte[] first = await Sampled(path, SmallLayout);
            byte[] second = await Sampled(path, SmallLayout);
            Assert.Equal(16, first.Length);
            Assert.Equal(first, second);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_digest_is_never_the_full_content_digest_for_the_same_file()
    {
        // The domain-separation prefix is what guarantees this. Without it a sampled digest of a small
        // file could equal its full digest, and a sampled value could be mistaken for an integrity hash.
        string path = WriteFile(1024);
        try
        {
            byte[] sampled = await Sampled(path, SmallLayout);
            var full = await Hasher.HashFileToBytesAsync(path, VerificationMethod.XxHash128);
            Assert.True(full.TryGetValue(out byte[]? fullDigest));
            Assert.NotEqual(fullDigest, sampled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_digest_changes_when_a_sampled_window_changes()
    {
        string path = WriteFile(64 * 1024);
        try
        {
            byte[] before = await Sampled(path, SmallLayout);

            // Offset 0 is always the head window, so this edit is certain to be seen.
            using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
            {
                fs.Position = 0;
                fs.WriteByte(0xFF);
                fs.WriteByte(0x00);
            }

            Assert.NotEqual(before, await Sampled(path, SmallLayout));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_digest_changes_when_only_the_length_changes()
    {
        // Length is folded into the digest prefix, so a size change alone always shows up — even if every
        // sampled window happened to land on identical bytes.
        string a = WriteFile(64 * 1024);
        string b = WriteFile(64 * 1024 + 1);
        try
        {
            Assert.NotEqual(await Sampled(a, SmallLayout), await Sampled(b, SmallLayout));
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task Identical_content_produces_identical_sampled_digests()
    {
        string a = WriteFile(256 * 1024, seed: 11);
        string b = Path.Combine(Path.GetTempPath(), "fm-sampled-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(a, b);
            Assert.Equal(await Sampled(a, SmallLayout), await Sampled(b, SmallLayout));
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public async Task A_file_inside_the_window_budget_is_hashed_whole()
    {
        // Below the budget the windows would cover everything anyway, so the implementation streams the
        // file. Assert the observable consequence: an edit ANYWHERE is caught, including the last byte.
        long length = SmallLayout.BudgetBytes - 1;
        string path = WriteFile(length);
        try
        {
            Assert.True(SmallLayout.CoversWholeFile(length));
            byte[] before = await Sampled(path, SmallLayout);

            using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
            {
                fs.Position = length - 1;
                fs.WriteByte(0xAB);
            }

            Assert.NotEqual(before, await Sampled(path, SmallLayout));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Different_layouts_produce_different_digests_for_the_same_file()
    {
        // The layout is in the digest prefix, so digests taken under different layouts can never be
        // compared by accident.
        string path = WriteFile(64 * 1024);
        try
        {
            byte[] narrow = await Sampled(path, SmallLayout);
            byte[] wide = await Sampled(path, SmallLayout with { InteriorWindowCount = 3 });
            Assert.NotEqual(narrow, wide);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_hash_MISSES_an_edit_between_its_windows_which_is_the_documented_limit()
    {
        // This is the trade-off spec §3.4.1 describes, pinned as a fact rather than left as prose. An
        // in-place edit that changes no bytes in any sampled window and does not change the file's length
        // is invisible to a sampled digest — which is exactly why the cheap methods are opt-in, why they
        // only apply above a size threshold, and why pairing them with a source-deleting OnSuccess raises a
        // blocking warning.
        //
        // If a future layout change makes this test FAIL, that is good news, not a regression: delete it
        // and update the spec's limitation wording to match.
        string path = WriteFile(64 * 1024);
        try
        {
            long[] windows = SmallLayout.WindowOffsets(new FileInfo(path).Length);
            Assert.NotEmpty(windows);

            // Derive an offset provably outside every window rather than hard-coding one, so the test
            // still means what it says if the geometry changes.
            long unsampled = -1;
            for (long candidate = 0; candidate < 64 * 1024; candidate += 512)
            {
                if (Array.TrueForAll(windows, o => candidate < o || candidate >= o + SmallLayout.WindowSizeBytes))
                {
                    unsampled = candidate;
                    break;
                }
            }
            Assert.True(unsampled >= 0, "the layout should leave gaps between windows at this file size");

            byte[] before = await Sampled(path, SmallLayout);
            using (FileStream fs = new(path, FileMode.Open, FileAccess.Write))
            {
                fs.Position = unsampled;
                fs.WriteByte(0x5A);
                fs.WriteByte(0xA5);
            }

            Assert.Equal(before, await Sampled(path, SmallLayout));

            // ...whereas a full hash of the same file does see it. This is the difference the user is
            // choosing between.
            var full = await Hasher.HashFileToBytesAsync(path, VerificationMethod.XxHash128);
            Assert.True(full.TryGetValue(out byte[]? afterFull));
            string copy = path + ".pristine";
            try
            {
                File.Copy(path, copy);
                using (FileStream fs = new(copy, FileMode.Open, FileAccess.Write))
                {
                    fs.Position = unsampled;
                    fs.WriteByte(0x00);
                    fs.WriteByte(0x00);
                }
                var otherFull = await Hasher.HashFileToBytesAsync(copy, VerificationMethod.XxHash128);
                Assert.True(otherFull.TryGetValue(out byte[]? otherDigest));
                Assert.NotEqual(afterFull, otherDigest);
            }
            finally { File.Delete(copy); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_hash_tolerates_a_file_shrinking_under_it()
    {
        // The sharing flags let another process truncate mid-hash. That must produce a digest, not a
        // failure — the caller's size comparison is what rejects the pair.
        string path = WriteFile(64 * 1024);
        try
        {
            using (FileStream truncate = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                truncate.SetLength(SmallLayout.WindowSizeBytes / 2);

            var hashed = await Hasher.HashSampledToBytesAsync(path, SmallLayout);
            Assert.True(hashed.IsSuccess);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_hash_of_an_empty_file_succeeds()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] digest = await Sampled(path, SmallLayout);
            Assert.Equal(16, digest.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_hash_of_a_missing_file_is_a_SourceUnreadable_error()
    {
        var hashed = await Hasher.HashSampledToBytesAsync(
            Path.Combine(Path.GetTempPath(), "fm-no-such-" + Guid.NewGuid().ToString("N")), SmallLayout);
        Assert.True(hashed.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.SourceUnreadable, error.Code);
    }

    [Fact]
    public async Task Sampled_hash_cancellation_yields_a_Canceled_result()
    {
        string path = WriteFile(64 * 1024);
        try
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            var hashed = await Hasher.HashSampledToBytesAsync(path, SmallLayout, cts.Token);
            Assert.True(hashed.IsCanceled);
            Assert.False(hashed.TryGetError(out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sampled_hash_unexpected_exception_is_a_traceable_failure_not_a_throw()
    {
        var hashed = await Hasher.HashSampledToBytesAsync(null!, SmallLayout);
        Assert.True(hashed.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.SourceUnreadable, error.Code);
        Assert.Contains(nameof(ArgumentNullException), error.Message);
    }

    [Fact]
    public void A_malformed_layout_throws_rather_than_returning_an_error()
    {
        // A bad layout is a programming error, not an I/O condition — it must not be laundered into a
        // JobError that a caller might treat as "this file is unreadable". The discard keeps these as
        // Action lambdas, which also pins that the throw is SYNCHRONOUS rather than a faulted task.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = Hasher.HashSampledToBytesAsync("x", new SampledHashLayout { WindowSizeBytes = 0, InteriorWindowCount = 2 });
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = Hasher.HashSampledToBytesAsync("x", new SampledHashLayout { WindowSizeBytes = 4096, InteriorWindowCount = -1 });
        });
    }
}
