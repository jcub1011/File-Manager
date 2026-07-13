using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FileManager.Core.Journal;

/// <summary>The mechanical spec §3.3 rollback sweep, shared by the live executor and crash
/// recovery. Never touches the source (I-SOURCE-RB — no source path is even an input). Best-effort:
/// one target's rollback failure never aborts the others. Owns the terminal <c>job-closed</c>
/// record (§4.7 step 4). The <see cref="IRollbackExecutor"/> surface is synchronous, so each I/O
/// step uses an inline 3-attempt best-effort retry (the equivalent of the async
/// <see cref="Placement.ITransientRetryPolicy"/> the live write path uses).</summary>
public sealed class RollbackExecutor(IJobJournal journal, TimeProvider time, ILogger<RollbackExecutor> logger) : IRollbackExecutor
{
    private const int MaxAttempts = 3;

    /// <summary>Backoff between rollback I/O attempts so a transient lock (AV / indexer / another
    /// handle) can clear before the retry. Test seam: unit tests set this to zero.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public Result<RollbackResult, JobError> Rollback(RollbackContext context, CancellationToken ct = default)
    {
        Guid jobId = context.JobId.Value;
        try
        {
            return RollbackCore(context, jobId, ct);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all: an unexpected fault (e.g. a malformed reconstructed path, a
            // SecurityException) must never leave the job OPEN — it would re-enter crash recovery on
            // every startup (a livelock). Log it and still write a terminal close so the job resolves.
            logger.LogError(ex, "Rollback of job {JobId} failed unexpectedly; forcing terminal close", jobId);
            Journal(new JobClosedRecord { JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(), Outcome = JobOutcome.RollbackFailed });
            return new JobError { Code = JobErrorCode.RollbackIncomplete, Message = $"rollback failed unexpectedly: {ex.Message}" };
        }
    }

    private Result<RollbackResult, JobError> RollbackCore(RollbackContext context, Guid jobId, CancellationToken ct)
    {
        // 1. Journal rollback-begin (fsync). (Cancelling/awaiting outstanding target tasks is the
        //    live executor's concern; recovery reconstructs state and calls in with no live tasks.)
        JobError? beginError = Journal(new RollbackBeginRecord
        {
            JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(),
            Reason = context.Cause.Message, FailedTargetIndex = context.Cause.TargetIndex,
        });
        if (beginError is not null)
            return beginError;

        var residuals = new List<string>();
        var keepStagingDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. Per target, by state.
        foreach (TargetRollbackItem target in context.Targets)
        {
            (RollbackAction action, string? error) = RevertTarget(target, context.OverwriteHandling);

            if (error is not null)
            {
                residuals.Add(target.FinalPath ?? target.StagedPath ?? $"target[{target.TargetIndex}]");
                if (target.StagedPath is not null)
                    keepStagingDirs.Add(StagingDirOf(target.StagedPath));
            }

            JobError? recError = Journal(new TargetRolledBackRecord
            {
                JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(),
                TargetIndex = target.TargetIndex, Action = action, Error = error,
            });
            if (recError is not null)
                return recError;
        }

        // 3. Delete the workspace; delete each staging dir only if every staged file in it was
        //    restored (I-STAGING-KEEP). A staging dir with an unrestored file is left for recovery
        //    to quarantine — never deleted here.
        TryDeleteDirectory(context.WorkspaceDir);
        foreach (TargetRollbackItem target in context.Targets)
        {
            if (target.StagedPath is null)
                continue;
            string stagingDir = StagingDirOf(target.StagedPath);
            if (!keepStagingDirs.Contains(stagingDir))
                TryDeleteDirectory(stagingDir);
        }

        // 4. Terminal journal.
        bool complete = residuals.Count == 0;
        JobError? closeError = Journal(new JobClosedRecord
        {
            JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(),
            Outcome = complete ? JobOutcome.Failed : JobOutcome.RollbackFailed,
        });
        if (closeError is not null)
            return closeError;

        if (!complete)
            logger.LogWarning("Rollback of job {JobId} left {Count} residual path(s): {Paths}", jobId, residuals.Count, string.Join(", ", residuals));

        return new RollbackResult { Complete = complete, ResidualPaths = residuals };
    }

    private (RollbackAction Action, string? Error) RevertTarget(TargetRollbackItem target, OverwriteHandling overwrite)
    {
        switch (target.State)
        {
            case TargetState.Pending:
            case TargetState.SatisfiedUnchanged:   // content predates the job — never reverted
            case TargetState.SkippedConflict:
            case TargetState.RolledBack:
            case TargetState.RollbackFailed:
                return (RollbackAction.None, null);

            case TargetState.TempWriting:
            case TargetState.TempWritten:
            case TargetState.Verified:
                return (RollbackAction.RemovedTemp, TryDeleteFile(target.TempPath));

            case TargetState.Staged:
                // Prior moved out, rename not yet done (two-step fallback): final is absent, a plain
                // move restores it; then remove the temp.
                string? stageError = TryMove(target.StagedPath, target.FinalPath, overwrite: false)
                    ?? TryDeleteFile(target.TempPath);
                return (RollbackAction.RestoredStagedBeforePlacement, stageError);

            case TargetState.Placed:
                return RevertPlaced(target, overwrite);

            default:
                return (RollbackAction.None, $"unknown target state {target.State}");
        }
    }

    private (RollbackAction Action, string? Error) RevertPlaced(TargetRollbackItem target, OverwriteHandling overwrite)
    {
        if (overwrite == OverwriteHandling.StageOverwrites && target.FinalExistedBeforeJob)
        {
            // Atomic restore of the prior version, no absent-window.
            string? error = TryReplace(target.StagedPath, target.FinalPath);
            return (RollbackAction.UnplacedAndRestored, error);
        }
        if (!target.FinalExistedBeforeJob)
        {
            // Fresh file — delete it so no half-finished set remains.
            return (RollbackAction.UnplacedNoPrior, TryDeleteFile(target.FinalPath));
        }
        // DirectOverwrite + prior existed: the prior version is unrecoverable by §6.2's own
        // definition; deleting the new file too would destroy the only content at that name.
        logger.LogWarning("Leaving placed file \"{Path}\" — DirectOverwrite rollback cannot restore the overwritten prior version", target.FinalPath);
        return (RollbackAction.LeftInPlaceUnrecoverable, null);
    }

    // --- best-effort I/O with an inline 3-attempt retry; returns null on success, a message on final failure ---

    private string? TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        return Attempt($"delete \"{path}\"", () => { if (File.Exists(path)) File.Delete(path); });
    }

    private string? TryMove(string? from, string? to, bool overwrite)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return $"cannot move: missing path (from=\"{from}\", to=\"{to}\")";
        return Attempt($"move \"{from}\" → \"{to}\"", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite);
        });
    }

    private string? TryReplace(string? staged, string? final)
    {
        if (string.IsNullOrEmpty(staged) || string.IsNullOrEmpty(final))
            return $"cannot restore: missing path (staged=\"{staged}\", final=\"{final}\")";
        return Attempt($"restore \"{staged}\" → \"{final}\"", () =>
        {
            try
            {
                File.Replace(staged, final, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Fallback: the final currently holds the (bad) new file; overwrite it with staged.
                File.Move(staged, final, overwrite: true);
            }
        });
    }

    private string? Attempt(string description, Action io)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                io();
                return null;
            }
            catch (Exception ex)
            {
                // Catch-all (directive): a rollback step must never throw out of here — every failure
                // becomes a residual string the caller records, so the sweep continues and the job
                // still closes. IOException/UnauthorizedAccessException are the expected transients.
                if (attempt == MaxAttempts)
                {
                    logger.LogWarning(ex, "Rollback step failed after {Attempts} attempts: {Description}", MaxAttempts, description);
                    return $"{description}: {ex.Message}";
                }
                if (RetryDelay > TimeSpan.Zero)
                    Thread.Sleep(RetryDelay);
            }
        }
        return null;
    }

    private void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete directory {Dir} during rollback", dir);
        }
    }

    private static string StagingDirOf(string stagedPath) => Path.GetDirectoryName(stagedPath) ?? stagedPath;

    private JobError? Journal(JournalRecord record)
    {
        Result result = journal.Append(record);
        if (result.TryGetError(out string? error))
            return new JobError { Code = JobErrorCode.JournalWriteFailed, Message = error };
        return null;
    }
}
