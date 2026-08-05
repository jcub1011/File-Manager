using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

/// <summary>Streaming content hasher — never whole-file in memory (spec §11). The algorithm is
/// chosen per call from the job's <see cref="VerificationMethod"/> (XxHash128 by default, SHA-256
/// when selected). Opens with the widest sharing so hashing never blocks a concurrent writer or
/// deleter; a file that changes under the hash is caught by the caller's size/verify logic, not
/// here.</summary>
public sealed class FileHasher(ILogger<FileHasher> logger) : IFileHasher
{
    private const int MaxBufferSize = 1024 * 1024;
    private const int MinBufferSize = 64 * 1024;

    public async Task<Result<string, JobError>> HashFileAsync(string path, VerificationMethod method, CancellationToken ct = default)
    {
        var bytes = await HashFileToBytesAsync(path, method, ct).ConfigureAwait(false);
        if (bytes.IsCanceled)
            return Result<string, JobError>.Canceled();
        if (bytes.TryGetError(out JobError? error))
            return error;
        bytes.TryGetValue(out byte[]? digest);
        return Convert.ToHexString(digest!);
    }

    public async Task<Result<byte[], JobError>> HashFileToBytesAsync(string path, VerificationMethod method, CancellationToken ct = default)
    {
        byte[]? buffer = null;
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Rent one reusable buffer rather than letting FileStream allocate a fresh internal
            // buffer per call — a large buffer lands on the LOH and forces Gen2 GCs even for tiny
            // files. Size it to the file (clamped) so a 4 KiB file doesn't touch a 1 MiB buffer,
            // while large files keep the 1 MiB reads the buffer-size benchmark showed are optimal.
            // FileStream does no internal buffering (bufferSize 1); we read into the rented buffer
            // and feed the accumulator, keeping the "never whole-file in memory" guarantee.
            long length = SafeLength(stream);
            int bufferSize = (int)Math.Clamp(length, MinBufferSize, MaxBufferSize);
            buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            using IHashAccumulator hasher = CreateAccumulator(method);

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                hasher.Append(buffer.AsSpan(0, read));

            return hasher.GetHashAndReset();
        }
        catch (OperationCanceledException)
        {
            return Result<byte[], JobError>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not hash {Path}", path);
            return new JobError
            {
                Code = JobErrorCode.SourceUnreadable,
                Message = $"could not hash \"{path}\": {ex.Message}",
                Path = path,
            };
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Hashing {Path} failed unexpectedly", path);
            return new JobError
            {
                Code = JobErrorCode.SourceUnreadable,
                Message = $"could not hash \"{path}\": {ex.GetType().Name}: {ex.Message}",
                Path = path,
            };
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<Result<byte[], JobError>> HashSampledToBytesAsync(
        string path, SampledHashLayout layout, CancellationToken ct = default)
    {
        // A malformed layout is a programming error, not an I/O failure — throw synchronously (as
        // CreateAccumulator does for a non-hash method) rather than returning it as a JobError.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layout.WindowSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(layout.InteriorWindowCount);
        return HashSampledCoreAsync(path, layout, ct);
    }

    private async Task<Result<byte[], JobError>> HashSampledCoreAsync(
        string path, SampledHashLayout layout, CancellationToken ct)
    {
        byte[]? buffer = null;
        SafeFileHandle? handle = null;
        try
        {
            // A handle + RandomAccess rather than a FileStream: the reads are at computed offsets, so
            // there is no sequential stream to seek and no FileStream position state to keep in sync.
            // RandomAccess over SequentialScan for the same reason. Sharing flags match the streaming
            // path — hashing must never block a concurrent writer or deleter.
            handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            long length = RandomAccess.GetLength(handle);
            XxHash128 hash = CreateSampledAccumulator(layout, length);

            buffer = ArrayPool<byte>.Shared.Rent(layout.WindowSizeBytes);

            if (layout.CoversWholeFile(length))
            {
                // Windows would cover all of it anyway: read it straight through. The digest keeps the
                // sampled framing, so it stays a sampled digest — comparable with other sampled digests
                // of the same layout, and still not interchangeable with a full-content hash.
                long offset = 0;
                while (offset < length)
                {
                    int read = await RandomAccess
                        .ReadAsync(handle, buffer.AsMemory(0, layout.WindowSizeBytes), offset, ct)
                        .ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    hash.Append(buffer.AsSpan(0, read));
                    offset += read;
                }
            }
            else
            {
                foreach (long offset in layout.WindowOffsets(length))
                    await AppendWindowAsync(handle, hash, buffer, offset, layout.WindowSizeBytes, ct)
                        .ConfigureAwait(false);
            }

            return hash.GetHashAndReset();
        }
        catch (OperationCanceledException)
        {
            return Result<byte[], JobError>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not sample-hash {Path}", path);
            return new JobError
            {
                Code = JobErrorCode.SourceUnreadable,
                Message = $"could not hash \"{path}\": {ex.Message}",
                Path = path,
            };
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Sample-hashing {Path} failed unexpectedly", path);
            return new JobError
            {
                Code = JobErrorCode.SourceUnreadable,
                Message = $"could not hash \"{path}\": {ex.GetType().Name}: {ex.Message}",
                Path = path,
            };
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
            handle?.Dispose();
        }
    }

    /// <summary>Reads one window in full and appends it. A short read means the file shrank under us (the
    /// sharing flags allow a concurrent writer): append what arrived and carry on rather than failing —
    /// the length is already in the digest, so a size change makes this digest differ regardless.</summary>
    private static async Task AppendWindowAsync(
        SafeFileHandle handle, XxHash128 hash, byte[] buffer, long offset, int windowSize, CancellationToken ct)
    {
        int filled = 0;
        while (filled < windowSize)
        {
            int read = await RandomAccess
                .ReadAsync(handle, buffer.AsMemory(filled, windowSize - filled), offset + filled, ct)
                .ConfigureAwait(false);
            if (read <= 0)
                break;
            filled += read;
        }
        hash.Append(buffer.AsSpan(0, filled));
    }

    // The domain-separation prefix, and the reason a sampled digest can never be mistaken for a full-file
    // one: the tag, the layout that produced it, and the file's length all feed the hash before any content
    // does. Folding the length in also means a size change alone always changes the digest. Kept in a
    // non-async method so the stackalloc is legal.
    private const int SampledPrefixLength = 6 + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(long);

    private static XxHash128 CreateSampledAccumulator(SampledHashLayout layout, long fileLength)
    {
        XxHash128 hash = new();
        Span<byte> prefix = stackalloc byte[SampledPrefixLength];
        "FMSAMP"u8.CopyTo(prefix);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[6..], SampledHashLayout.Version);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[10..], layout.WindowSizeBytes);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[14..], layout.InteriorWindowCount);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[18..], fileLength);
        hash.Append(prefix);
        return hash;
    }

    // A stream whose length can't be queried (rare for regular files) just falls back to the max
    // buffer — correctness is unaffected, only the initial rent size.
    private static long SafeLength(FileStream stream)
    {
        try { return stream.Length; }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { return MaxBufferSize; }
    }

    // Unifies the two hash families the streaming loop feeds: XxHash128 is a
    // NonCryptographicHashAlgorithm (Append/GetHashAndReset), SHA-256 an IncrementalHash
    // (AppendData/GetHashAndReset). Only hash-based verification methods reach here — the callers
    // gate out None/SizeTimestamp before hashing.
    private static IHashAccumulator CreateAccumulator(VerificationMethod method) => method switch
    {
        VerificationMethod.XxHash128 => new XxHashAccumulator(),
        VerificationMethod.Sha256 => new IncrementalHashAccumulator(HashAlgorithmName.SHA256),
        _ => throw new ArgumentOutOfRangeException(
            nameof(method), method, "not a content-hash algorithm"),
    };

    private interface IHashAccumulator : IDisposable
    {
        void Append(ReadOnlySpan<byte> data);
        byte[] GetHashAndReset();
    }

    private sealed class XxHashAccumulator : IHashAccumulator
    {
        private readonly XxHash128 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public byte[] GetHashAndReset() => _hash.GetHashAndReset();
        public void Dispose() { }
    }

    private sealed class IncrementalHashAccumulator(HashAlgorithmName algorithm) : IHashAccumulator
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(algorithm);
        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);
        public byte[] GetHashAndReset() => _hash.GetHashAndReset();
        public void Dispose() => _hash.Dispose();
    }
}
