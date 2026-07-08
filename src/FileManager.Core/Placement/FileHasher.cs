using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

/// <summary>Streaming SHA-256 — never whole-file in memory (spec §11). Opens with the widest
/// sharing so hashing never blocks a concurrent writer or deleter; a file that changes under
/// the hash is caught by the caller's size/verify logic, not here.</summary>
public sealed class FileHasher(ILogger<FileHasher> logger) : IFileHasher
{
    private const int BufferSize = 1024 * 1024;

    public async Task<Result<string, JobError>> HashFileAsync(string path, CancellationToken ct = default)
    {
        // Rent one reusable buffer rather than letting FileStream allocate a fresh 1 MiB internal
        // buffer per call — that buffer lands on the LOH and forces Gen2 GCs even for tiny files.
        // FileStream does no internal buffering (bufferSize 1); we read into the rented buffer and
        // feed an IncrementalHash, keeping the "never whole-file in memory" guarantee.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                hasher.AppendData(buffer, 0, read);

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (OperationCanceledException)
        {
            return Result<string, JobError>.Canceled();
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
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
