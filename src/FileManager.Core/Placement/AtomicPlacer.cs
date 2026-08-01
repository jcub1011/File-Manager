using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

/// <summary>The unchanged-file short-circuit (spec §3.4.1) and the
/// write-temp → fsync → verify-by-read-back → [stage] → atomic-rename sequence (spec §4 Phases 4–5,
/// §6.2). The most safety-critical code in the system; each numbered step maps to a §7.2 journal
/// row and upholds I-RENAME, I-WAL, I-PRIOR, and I-VERIFY-READBACK. One target per call.</summary>
public sealed class AtomicPlacer(
    IFileHasher hasher,
    IJobJournal journal,
    SelfWriteSuppressionRegistry suppression,
    ITransientRetryPolicy retry,
    IMetadataPreserver metadata,
    SourcePriorityRegistry priorities,
    TimeProvider time,
    ILogger<AtomicPlacer> logger) : IAtomicPlacer
{
    private const int CopyBufferSize = 1024 * 1024;
    private const string TempSuffix = ".fmtmp-";

    /// <summary>Timestamp comparison tolerance for the VerificationMethod.None unchanged-check —
    /// FAT/exFAT round last-write times to ~2 s, so an exact-tick compare would never short-circuit.</summary>
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public async Task<Result<UnchangedCheckResult, JobError>> CheckUnchangedAsync(
        JobExecution execution, int targetIndex, string finalPath, CancellationToken ct = default)
    {
        SealedOutput? output = execution.Output;
        if (output is null)
            return Unusable("output not sealed before unchanged-check", finalPath, targetIndex);

        try
        {
            if (!File.Exists(finalPath))
                return UnchangedCheckResult.NoExistingFile;

            var existing = new FileInfo(finalPath);
            if (existing.Length != output.SizeBytes)
                return UnchangedCheckResult.ExistsDifferent;

            VerificationMethod method = execution.Plan.Policies.Verification;
            if (method is VerificationMethod.Sha256 or VerificationMethod.XxHash128)
            {
                Result<string, JobError> hashed = await hasher.HashFileAsync(finalPath, method, ct).ConfigureAwait(false);
                if (hashed.IsCanceled)
                    return Result<UnchangedCheckResult, JobError>.Canceled();
                if (hashed.TryGetError(out JobError? hashError))
                    return hashError;
                hashed.TryGetValue(out string? existingHash);
                if (!string.Equals(existingHash, output.ContentHash, StringComparison.OrdinalIgnoreCase))
                    return UnchangedCheckResult.ExistsDifferent;
            }
            else
            {
                // VerificationMethod.None: best-effort — same size and (within FAT/exFAT rounding)
                // same last-write time.
                TimeSpan drift = (File.GetLastWriteTimeUtc(finalPath) - output.SourceLastWriteUtc.UtcDateTime).Duration();
                if (drift > TimestampTolerance)
                    return UnchangedCheckResult.ExistsDifferent;
            }

            // Unchanged: satisfy the target without writing (spec §3.4.1). Journal it, mark the
            // target, and record priority so a later lower/higher source resolves correctly.
            JobError? journalError = Journal(new TargetUnchangedRecord
            {
                JobId = execution.Plan.JobId.Value,
                Seq = 0,
                AtUtc = time.GetUtcNow(),
                TargetIndex = targetIndex,
                FinalPath = finalPath,
            });
            if (journalError is not null)
                return journalError;

            execution.Targets[targetIndex].State = TargetState.SatisfiedUnchanged;
            execution.Targets[targetIndex].FinalPath = finalPath;
            RecordPriority(execution.Plan, finalPath);
            return UnchangedCheckResult.Unchanged;
        }
        catch (OperationCanceledException)
        {
            return Result<UnchangedCheckResult, JobError>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JobError { Code = JobErrorCode.TargetWriteFailed, Message = $"unchanged-check failed for \"{finalPath}\": {ex.Message}", Path = finalPath, TargetIndex = targetIndex };
        }
        catch (Exception ex)
        {
            // Last-resort catch-all at this task boundary: an unexpected fault must be logged and
            // surfaced as a JobError, never propagate out of the safety-critical placer.
            logger.LogError(ex, "Unexpected error during unchanged-check for \"{Final}\"", finalPath);
            return new JobError { Code = JobErrorCode.TargetWriteFailed, Message = $"unchanged-check failed unexpectedly for \"{finalPath}\": {ex.Message}", Path = finalPath, TargetIndex = targetIndex };
        }
    }

    public async Task<Result<PlacementResult, JobError>> PlaceTargetAsync(PlacementRequest request, CancellationToken ct = default)
    {
        JobExecution execution = request.Execution;
        int index = request.TargetIndex;
        SealedOutput output = request.Output;
        TargetProgress tp = execution.Targets[index];
        Guid jobId = execution.Plan.JobId.Value;
        string jobShort = execution.Plan.JobId.Short;

        string finalPath = request.FinalPath;
        string targetDir = Path.GetDirectoryName(finalPath) ?? throw new ArgumentException("final path has no directory", nameof(request));
        string tempPath = finalPath + TempSuffix + jobShort;
        string stagedPath = InfrastructurePaths.StagedPathFor(tp.Plan.TargetRoot, jobId, index, Path.GetFileName(finalPath));

        SuppressionToken? tempTok = null, finalTok = null, stagedTok = null;
        try
        {
            // 1. Journal target-write-begin (fsync) BEFORE the temp exists (I-WAL); register the
            //    engine's own write paths so the watcher never echoes them back.
            JobError? journalError = Journal(new TargetWriteBeginRecord
            {
                JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(),
                TargetIndex = index, TempPath = tempPath, FinalPath = finalPath, FinalExisted = request.FinalExists,
            });
            if (journalError is not null)
                return journalError;

            tempTok = RegisterSuppression(tempPath);
            finalTok = RegisterSuppression(finalPath);
            stagedTok = RegisterSuppression(stagedPath);

            tp.TempPath = tempPath;
            tp.FinalPath = finalPath;
            tp.FinalExistedBeforeJob = request.FinalExists;
            tp.State = TargetState.TempWriting;

            // 2–3. Copy workspace output → temp, then Flush(flushToDisk:true) so "verified" attests
            //      to disk contents, not cache (I-VERIFY-READBACK).
            Result<bool, JobError> copied = await retry.ExecuteAsync(
                "copy-to-temp", c => CopyToTempAsync(output.Path, tempPath, targetDir, c), ct).ConfigureAwait(false);
            if (copied.IsCanceled) return Result<PlacementResult, JobError>.Canceled();
            if (copied.TryGetError(out JobError? copyError)) return copyError;
            tp.State = TargetState.TempWritten;

            // 4. Read-back verify the temp THROUGH the target volume.
            Result<bool, JobError> verified = await retry.ExecuteAsync(
                "verify-readback", c => VerifyAsync(tempPath, output, request.Verification, index, c), ct).ConfigureAwait(false);
            if (verified.IsCanceled) return Result<PlacementResult, JobError>.Canceled();
            if (verified.TryGetError(out JobError? verifyError)) return verifyError;

            journalError = Journal(new TargetVerifiedRecord { JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(), TargetIndex = index });
            if (journalError is not null) return journalError;
            tp.State = TargetState.Verified;

            // Apply best-effort metadata to the temp before the rename (spec §6.4). The POLICY decides
            // what a failure means — "best-effort" is exactly what MetadataOnConflict.WarnAndContinue
            // asks for, and failing the job regardless of the setting made the policy meaningless: a
            // verified copy was thrown away over an attribute the profile said to warn about.
            MetadataOnConflict metadataPolicy = execution.Plan.Policies.MetadataOnConflict;
            Result metaResult = metadata.Apply(execution.Plan.Source.Path, tempPath, metadataPolicy);
            if (metaResult.TryGetError(out string? metaError))
            {
                if (metadataPolicy == MetadataOnConflict.FailJob)
                    return new JobError { Code = JobErrorCode.MetadataConflict, Message = metaError, Path = finalPath, TargetIndex = index };
                logger.LogWarning(
                    "Job {JobId} target {Index}: metadata could not be applied to \"{Final}\" ({Error}); continuing per MetadataOnConflict={Policy}",
                    jobShort, index, finalPath, metaError, metadataPolicy);
            }

            // 5. Place. For a staged overwrite the staging intent is journaled and state set ONCE
            //    here — before the retried move — so a transient replace failure can never emit
            //    duplicate target-staged rows or re-mutate target state on each retry.
            if (request.FinalExists && request.OverwriteHandling == OverwriteHandling.StageOverwrites)
            {
                JobError? stageJournalError = Journal(new TargetStagedRecord
                {
                    JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(),
                    TargetIndex = index, FinalPath = finalPath, StagedPath = stagedPath,
                });
                if (stageJournalError is not null)
                    return stageJournalError;

                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                tp.StagedPath = stagedPath;
                tp.State = TargetState.Staged;
            }

            Result<bool, JobError> placed = await retry.ExecuteAsync(
                "place", c => PlaceAsync(request, tempPath, finalPath, stagedPath, c), ct).ConfigureAwait(false);
            if (placed.IsCanceled) return Result<PlacementResult, JobError>.Canceled();
            if (placed.TryGetError(out JobError? placeError)) return placeError;

            // 6. Journal target-placed; record session priority.
            journalError = Journal(new TargetPlacedRecord { JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(), TargetIndex = index });
            if (journalError is not null) return journalError;
            tp.State = TargetState.Placed;
            RecordPriority(execution.Plan, finalPath);

            return new PlacementResult { FinalState = TargetState.Placed, StagedPath = tp.StagedPath };
        }
        catch (OperationCanceledException)
        {
            return Result<PlacementResult, JobError>.Canceled();
        }
        catch (Exception ex)
        {
            // Last-resort catch-all at this task boundary: the most safety-critical method must never
            // let an unexpected exception escape uncaught — log it and surface a JobError.
            logger.LogError(ex, "Unexpected error placing target {Index} of job {JobId}", index, jobId);
            return new JobError { Code = JobErrorCode.PlacementFailed, Message = $"unexpected placement error for \"{finalPath}\": {ex.Message}", Path = finalPath, TargetIndex = index };
        }
        finally
        {
            // Start the linger window on the engine's own write paths (§4.3 step 9).
            tempTok?.Release(SelfWriteSuppressionRegistry.DefaultLinger);
            finalTok?.Release(SelfWriteSuppressionRegistry.DefaultLinger);
            stagedTok?.Release(SelfWriteSuppressionRegistry.DefaultLinger);
        }
    }

    private async Task<Result<bool, JobError>> CopyToTempAsync(string sourcePath, string tempPath, string targetDir, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            await using FileStream src = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (FileStream dst = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
            {
                await src.CopyToAsync(dst, CopyBufferSize, ct).ConfigureAwait(false);
                dst.Flush(flushToDisk: true);   // the fsync point (I-VERIFY-READBACK)
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return Result<bool, JobError>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JobError { Code = JobErrorCode.TargetWriteFailed, Message = $"could not write temp \"{tempPath}\": {ex.Message}", Path = tempPath };
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): surface as a JobError, never an unlogged throw.
            logger.LogError(ex, "Unexpected error writing temp \"{Temp}\"", tempPath);
            return new JobError { Code = JobErrorCode.TargetWriteFailed, Message = $"could not write temp \"{tempPath}\": {ex.GetType().Name}: {ex.Message}", Path = tempPath };
        }
    }

    private async Task<Result<bool, JobError>> VerifyAsync(string tempPath, SealedOutput output, VerificationMethod method, int index, CancellationToken ct)
    {
        try
        {
            if (method == VerificationMethod.None)
            {
                // Length check only (no reference hash exists).
                return new FileInfo(tempPath).Length == output.SizeBytes
                    ? true
                    : new JobError { Code = JobErrorCode.VerificationMismatch, Message = $"temp \"{tempPath}\" length {new FileInfo(tempPath).Length} != expected {output.SizeBytes}", Path = tempPath, TargetIndex = index };
            }

            Result<string, JobError> hashed = await hasher.HashFileAsync(tempPath, method, ct).ConfigureAwait(false);
            if (hashed.IsCanceled) return Result<bool, JobError>.Canceled();
            if (hashed.TryGetError(out JobError? error)) return error;
            hashed.TryGetValue(out string? actual);
            return string.Equals(actual, output.ContentHash, StringComparison.OrdinalIgnoreCase)
                ? true
                : new JobError { Code = JobErrorCode.VerificationMismatch, Message = $"read-back hash mismatch at \"{tempPath}\"", Path = tempPath, TargetIndex = index };
        }
        catch (OperationCanceledException)
        {
            return Result<bool, JobError>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The VerificationMethod.None branch touches FileInfo.Length, which can throw an I/O
            // error the try above would otherwise let escape. Treat as a transient write failure.
            return new JobError { Code = JobErrorCode.TargetWriteFailed, Message = $"read-back verify failed for \"{tempPath}\": {ex.Message}", Path = tempPath, TargetIndex = index };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error verifying temp \"{Temp}\"", tempPath);
            return new JobError { Code = JobErrorCode.TargetWriteFailed, Message = $"read-back verify failed unexpectedly for \"{tempPath}\": {ex.Message}", Path = tempPath, TargetIndex = index };
        }
    }

    private Task<Result<bool, JobError>> PlaceAsync(
        PlacementRequest request, string tempPath, string finalPath, string stagedPath, CancellationToken ct)
    {
        try
        {
            if (!request.FinalExists)
            {
                // Fresh file: a plain move. An unexpected existing file fails loudly (impossible
                // under the path lock — failure would mean external interference).
                File.Move(tempPath, finalPath, overwrite: false);
            }
            else if (request.OverwriteHandling == OverwriteHandling.StageOverwrites)
            {
                // Staging intent was journaled and target state set once by the caller (before this
                // retried step); here we only perform the idempotent atomic replace.
                StagedReplace.Execute(tempPath, finalPath, stagedPath, logger);
            }
            else
            {
                // DirectOverwrite: atomic replace on NTFS (MOVEFILE_REPLACE_EXISTING).
                File.Move(tempPath, finalPath, overwrite: true);
            }
            return Task.FromResult<Result<bool, JobError>>(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<Result<bool, JobError>>(new JobError
            {
                Code = JobErrorCode.PlacementFailed,
                Message = $"could not place \"{finalPath}\": {ex.Message}",
                Path = finalPath,
                TargetIndex = request.TargetIndex,
            });
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): surface as a JobError, never an unlogged throw.
            logger.LogError(ex, "Unexpected error placing \"{Final}\"", finalPath);
            return Task.FromResult<Result<bool, JobError>>(new JobError
            {
                Code = JobErrorCode.PlacementFailed,
                Message = $"could not place \"{finalPath}\": {ex.GetType().Name}: {ex.Message}",
                Path = finalPath,
                TargetIndex = request.TargetIndex,
            });
        }
    }

    private SuppressionToken RegisterSuppression(string path)
    {
        // Suppression keys are normalized; an unnormalizable path just isn't suppressed (the
        // watcher's infra exclusions still cover *.fmtmp-* and .fm_staging).
        Result<NormalizedPath, JobError> normalized = NormalizedPath.Create(path);
        NormalizedPath key = normalized.TryGetValue(out NormalizedPath p) ? p : default;
        return suppression.Register(key, default);
    }

    private void RecordPriority(JobPlan plan, string finalPath)
    {
        Result<NormalizedPath, JobError> normalized = NormalizedPath.Create(finalPath);
        if (normalized.TryGetValue(out NormalizedPath key))
            // plan.PriorityIndex, not a locally re-derived rank: the resolver reads the registry with
            // the same value, and two independent derivations are how the two sides drifted apart.
            priorities.RecordPlacement(plan.ProfileId, key, plan.PriorityIndex);
    }

    private JobError? Journal(JournalRecord record)
    {
        Result result = journal.Append(record);
        if (result.TryGetError(out string? error))
            return new JobError { Code = JobErrorCode.JournalWriteFailed, Message = error, Path = null };
        return null;
    }

    private static Result<UnchangedCheckResult, JobError> Unusable(string message, string path, int index) =>
        new JobError { Code = JobErrorCode.TargetWriteFailed, Message = message, Path = path, TargetIndex = index };
}
