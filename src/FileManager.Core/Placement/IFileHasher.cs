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

    /// <summary>Digest (16 bytes, XxHash128) over the file's LENGTH plus a bounded set of windows —
    /// <paramref name="layout"/> decides which — instead of its whole content. Reads
    /// <see cref="SampledHashLayout.BytesRead"/> bytes however large the file is, which is the entire
    /// point: it exists so the §3.4.1 unchanged-check can decide about a multi-gigabyte file without
    /// reading it end to end.
    ///
    /// <para>Always XxHash128, regardless of the job's <see cref="VerificationMethod"/> — a sampled digest
    /// is never an integrity attestation, so there is nothing for SHA-256 to buy here, and this makes the
    /// probe equally cheap on SHA-256 profiles.</para>
    ///
    /// <para><b>Two digests are comparable only under an identical layout</b>, and a sampled digest is
    /// NEVER interchangeable with a full-content digest — the returned value carries a layout-specific
    /// domain-separation prefix so the two can never collide. Never store one in
    /// <c>SealedOutput.ContentHash</c> or any journal field: crash recovery and rollback compare those
    /// against full hashes.</para>
    ///
    /// <para>Comparing two sampled digests is EXACT when they differ (sampled bytes that differ prove the
    /// files differ) and probabilistic when they match.</para></summary>
    Task<Result<byte[], JobError>> HashSampledToBytesAsync(string path, SampledHashLayout layout, CancellationToken ct = default);
}
