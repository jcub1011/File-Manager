using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

public interface IFileHasher
{
    Task<Result<string, JobError>> HashFileAsync(string path, CancellationToken ct = default);

    /// <summary>Same streaming SHA-256 as <see cref="HashFileAsync"/> but returns the raw 32-byte
    /// digest, letting callers compare digests without allocating a hex string per file. The string
    /// overload delegates here.</summary>
    Task<Result<byte[], JobError>> HashFileToBytesAsync(string path, CancellationToken ct = default);
}
