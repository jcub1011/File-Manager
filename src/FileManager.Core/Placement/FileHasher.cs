using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
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
