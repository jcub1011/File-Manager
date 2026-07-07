using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
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
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash);
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
    }
}
