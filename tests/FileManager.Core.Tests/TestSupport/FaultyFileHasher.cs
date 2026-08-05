using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Wraps the real <see cref="FileHasher"/> and, on request, corrupts or fails hashes for paths
/// matching a predicate. Two failure shapes matter and behave very differently by design:
/// <list type="bullet">
/// <item><description><see cref="CorruptPathsMatching"/> returns a <em>wrong but valid</em> hash — the
/// engine must read this as data corruption (<c>VerificationMismatch</c>), which is deterministic and
/// must NOT be retried.</description></item>
/// <item><description><see cref="FailTransientlyForPathsMatching"/> returns a transient I/O error code
/// for the first <see cref="TransientFailureCount"/> calls — the engine must retry these.</description></item>
/// </list>
/// Everything not matched is hashed for real, so a test only perturbs the one thing it is probing.</summary>
internal sealed class FaultyFileHasher(IFileHasher inner) : IFileHasher
{
    /// <summary>Paths matching this get a wrong-but-well-formed hash (simulated bit rot / bad write).</summary>
    public Func<string, bool>? CorruptPathsMatching { get; set; }

    /// <summary>Paths matching this fail with a transient code until the budget is used up.</summary>
    public Func<string, bool>? FailTransientlyForPathsMatching { get; set; }

    public int TransientFailureCount { get; set; }

    /// <summary>How many transient failures were actually served — lets a test prove the retry
    /// happened rather than inferring it from the outcome.</summary>
    public int TransientFailuresServed { get; private set; }

    private readonly Lock _gate = new();

    public async Task<Result<string, JobError>> HashFileAsync(
        string path, VerificationMethod method, CancellationToken ct = default)
    {
        if (TryTakeTransientFailure(path))
            return TransientError(path);
        if (CorruptPathsMatching?.Invoke(path) == true)
        {
            // A wrong hash of the right SHAPE: the engine must reject on value, not on format.
            var real = await inner.HashFileAsync(path, method, ct).ConfigureAwait(false);
            if (!real.TryGetValue(out string? hash))
                return real;
            return Corrupt(hash!);
        }
        return await inner.HashFileAsync(path, method, ct).ConfigureAwait(false);
    }

    public async Task<Result<byte[], JobError>> HashFileToBytesAsync(
        string path, VerificationMethod method, CancellationToken ct = default)
    {
        if (TryTakeTransientFailure(path))
            return TransientError(path);
        if (CorruptPathsMatching?.Invoke(path) == true)
        {
            var real = await inner.HashFileToBytesAsync(path, method, ct).ConfigureAwait(false);
            if (!real.TryGetValue(out byte[]? digest))
                return real;
            byte[] corrupted = (byte[])digest!.Clone();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }
        return await inner.HashFileToBytesAsync(path, method, ct).ConfigureAwait(false);
    }

    /// <summary>Same two injected shapes on the bounded-read identity probe, so a test can perturb the
    /// §3.4.1 unchanged-check without also perturbing the full hash that verifies the written copy.</summary>
    public async Task<Result<byte[], JobError>> HashSampledToBytesAsync(
        string path, SampledHashLayout layout, CancellationToken ct = default)
    {
        if (TryTakeTransientFailure(path))
            return TransientError(path);
        if (CorruptPathsMatching?.Invoke(path) == true)
        {
            var real = await inner.HashSampledToBytesAsync(path, layout, ct).ConfigureAwait(false);
            if (!real.TryGetValue(out byte[]? digest))
                return real;
            byte[] corrupted = (byte[])digest!.Clone();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }
        return await inner.HashSampledToBytesAsync(path, layout, ct).ConfigureAwait(false);
    }

    private bool TryTakeTransientFailure(string path)
    {
        if (FailTransientlyForPathsMatching?.Invoke(path) != true)
            return false;
        lock (_gate)
        {
            if (TransientFailuresServed >= TransientFailureCount)
                return false;
            TransientFailuresServed++;
            return true;
        }
    }

    private static JobError TransientError(string path) => new()
    {
        Code = JobErrorCode.TargetWriteFailed,   // classified transient by TransientErrors
        Message = "injected transient read failure",
        Path = path,
    };

    /// <summary>Flips the first hex digit, keeping length and alphabet valid.</summary>
    private static string Corrupt(string hash) =>
        (hash[0] == '0' ? '1' : '0') + hash[1..];
}
