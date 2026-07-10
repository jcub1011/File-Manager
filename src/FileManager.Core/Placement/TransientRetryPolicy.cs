using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

/// <summary>Spec §10 — a fixed 3-attempt × 2 s policy for transient I/O on target writes,
/// verification reads, and rollback steps. This is the <c>[seam]</c> for a future config-driven
/// backoff; call sites are already policy-agnostic. The clock is injected so tests need not wait
/// real seconds.</summary>
public sealed class TransientRetryPolicy(TimeProvider time, ILogger<TransientRetryPolicy> logger) : ITransientRetryPolicy
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    public async Task<Result<TValue, JobError>> ExecuteAsync<TValue>(
        string operationName,
        Func<CancellationToken, Task<Result<TValue, JobError>>> operation,
        CancellationToken ct = default)
    {
        Result<TValue, JobError> result = default;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            result = await operation(ct).ConfigureAwait(false);

            if (result.IsSuccess || result.IsCanceled)
                return result;

            if (attempt < MaxAttempts)
            {
                result.TryGetError(out JobError? error);
                logger.LogWarning("Transient failure on \"{Operation}\" (attempt {Attempt}/{Max}): {Message}; retrying in {Delay}s",
                    operationName, attempt, MaxAttempts, error?.Message, Delay.TotalSeconds);
                await Task.Delay(Delay, time, ct).ConfigureAwait(false);
            }
        }
        return result;   // the final failure
    }
}
