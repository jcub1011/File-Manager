using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

/// <summary>The set of <see cref="JobErrorCode"/>s that represent a transient I/O condition worth
/// retrying (spec §10). Everything else — a verification mismatch (data corruption), a metadata or
/// conflict-resolution failure, a journal write failure, insufficient disk — is deterministic:
/// retrying only wastes time and, worse, masks the real signal as "retrying". Shared so the retry
/// policy and any future retrying caller classify failures the same way.</summary>
public static class TransientErrors
{
    private static readonly HashSet<JobErrorCode> Codes =
    [
        JobErrorCode.TargetWriteFailed,   // copy-to-temp / read-back I/O error
        JobErrorCode.PlacementFailed,     // rename/replace I/O error
        JobErrorCode.StagingFailed,       // staging move I/O error
    ];

    public static bool IsTransient(JobErrorCode code) => Codes.Contains(code);
}

/// <summary>Spec §10 — a fixed 3-attempt × 2 s policy for transient I/O on target writes,
/// verification reads, and rollback steps. Only <see cref="TransientErrors.IsTransient"/> failures
/// are retried; a deterministic failure returns immediately. This is the <c>[seam]</c> for a future
/// config-driven backoff; call sites are already policy-agnostic. The clock is injected so tests
/// need not wait real seconds.</summary>
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
            try
            {
                result = await operation(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;   // cancellation keeps its existing throw-out contract
            }
            catch (Exception ex)
            {
                // Last-resort catch-all at this task boundary (directive): the delegate is arbitrary
                // caller code; an unexpected throw becomes a logged, deterministic (non-retried)
                // failure value rather than an exception two frames up.
                logger.LogError(ex, "Operation \"{Operation}\" threw unexpectedly (attempt {Attempt}/{Max})", operationName, attempt, MaxAttempts);
                return new JobError
                {
                    Code = JobErrorCode.PlacementFailed,
                    Message = $"\"{operationName}\" failed unexpectedly: {ex.GetType().Name}: {ex.Message}",
                };
            }

            if (result.IsSuccess || result.IsCanceled)
                return result;

            // Only retry genuinely transient I/O; a deterministic failure (verification mismatch,
            // metadata conflict, journal write, disk-full) is returned at once — no sleep, no
            // retry-shaped log that would hide corruption behind "retrying".
            result.TryGetError(out JobError? error);
            if (error is null || !TransientErrors.IsTransient(error.Code))
                return result;

            if (attempt < MaxAttempts)
            {
                logger.LogWarning("Transient failure on \"{Operation}\" (attempt {Attempt}/{Max}): {Message}; retrying in {Delay}s",
                    operationName, attempt, MaxAttempts, error.Message, Delay.TotalSeconds);
                await Task.Delay(Delay, time, ct).ConfigureAwait(false);
            }
        }
        return result;   // the final failure
    }
}
