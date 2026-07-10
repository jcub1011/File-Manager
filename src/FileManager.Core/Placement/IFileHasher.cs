using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

public interface IFileHasher
{
    /// <summary>Streams the file and returns its content hash as an uppercase hex string, using the
    /// algorithm selected by <paramref name="method"/> (XxHash128 → 32 hex chars, SHA-256 → 64).
    /// Callers compare with <see cref="System.StringComparison.OrdinalIgnoreCase"/>.</summary>
    Task<Result<string, JobError>> HashFileAsync(string path, VerificationMethod method, CancellationToken ct = default);

    /// <summary>Same streaming hash as <see cref="HashFileAsync"/> but returns the raw digest bytes
    /// (XxHash128 → 16, SHA-256 → 32), letting callers compare digests without allocating a hex
    /// string per file. The string overload delegates here.</summary>
    Task<Result<byte[], JobError>> HashFileToBytesAsync(string path, VerificationMethod method, CancellationToken ct = default);
}
