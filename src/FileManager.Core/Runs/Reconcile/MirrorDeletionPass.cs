using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Platform;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Runs.Reconcile;

/// <summary>Removes the destination files a <see cref="SyncMode.Mirror"/> run's plan identified as
/// orphans — to the Recycle Bin, never hard-deleted (spec §3.1.1).
///
/// <para><b>A sibling of <c>IJobExecutor</c>, not a producer into <c>ITriggerQueue</c></b> (§10.1).
/// It produces no payloads: routing deletions through the per-file job queue is precisely the model
/// the spec says cannot express them, because a deletion is not driven by a file arriving.</para>
///
/// <para><b>It does not compute anything.</b> The orphan set is READ BACK from the run's snapshot —
/// the same list the user approved, produced by the same planner that produced the preview. There is
/// no second diff to disagree with the first one. What this class owns is the part that must be
/// paranoid: the gates that refuse the whole pass, the per-path re-verification under lock, and the
/// write-ahead ordering that guarantees no file is ever removed without a durable record naming
/// it.</para>
///
/// <para>Never throws for a pass failure — every outcome, including a refusal, comes back as a
/// <see cref="MirrorDeletionResult"/>.</para></summary>
public sealed class MirrorDeletionPass(
    IJobJournal journal,
    PathLockRegistry lockRegistry,
    SelfWriteSuppressionRegistry suppression,
    ITrashService trash,
    IReconcileAuditLog audit,
    IPauseStateService pauseState,
    IJobLogStore jobLog,
    EngineConfig config,
    TimeProvider time,
    ILogger<MirrorDeletionPass> logger,
    IRunPauseGate? runPause = null) : IMirrorDeletionPass
{
    /// <summary>How often a paused pass re-checks. Matches the coordinator's own pause poll.</summary>
    private static readonly TimeSpan PausePollInterval = TimeSpan.FromMilliseconds(200);

    // Optional so every existing construction and test keeps its argument list and gets never-paused.
    private readonly IRunPauseGate _runPause = runPause ?? NullRunPauseGate.Instance;

    public async Task<MirrorDeletionResult> DeleteAsync(
        MirrorDeletionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await RunAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown or a run cancel BEFORE the deletion loop reached an orphan — a cancel inside one
            // is caught at the call site so its counts survive, so Deleted = 0 is the truth here.
            logger.LogInformation(
                "Mirror deletion pass {PassId} stopped on cancellation", request.PassId.Short);
            return Aborted(request, MirrorReconcileOutcome.AbortedMidPass, "the pass was cancelled");
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: a destructive pass must never surface as an unhandled throw.
            logger.LogError(ex, "Mirror deletion pass {PassId} failed unexpectedly", request.PassId.Short);
            return Aborted(request, MirrorReconcileOutcome.AbortedBeforeDeleting, ex.Message);
        }
    }

    private async Task<MirrorDeletionResult> RunAsync(MirrorDeletionRequest request, CancellationToken ct)
    {
        // ---- gates that refuse before anything is journalled or touched ---------------------------
        if (Refuse(request) is { } refusal)
        {
            logger.LogWarning(
                "Mirror deletion pass for run {RunId} refused: {Reason}", request.RunId, refusal);
            return Aborted(request, MirrorReconcileOutcome.AbortedBeforeDeleting, refusal);
        }

        if (request.Orphans.Count == 0)
            return new MirrorDeletionResult
            {
                PassId = request.PassId,
                Outcome = MirrorReconcileOutcome.NothingToDo,
            };

        // ---- open: from here the pass is on the record -------------------------------------------
        long orphanBytes = 0;
        foreach (RunDeleteItem orphan in request.Orphans)
            orphanBytes += orphan.SizeBytes;

        if (TryAppend(new MirrorReconcileOpenedRecord
        {
            JobId = request.PassId.Value,
            Seq = 0,
            AtUtc = time.GetUtcNow(),
            ProfileId = request.Profile.Id,
            RunId = request.RunId,
            Timing = request.Profile.Policies.MirrorDeletion,
            TargetRoots = [.. TargetRoots(request.Profile)],
            OrphanCount = request.Orphans.Count,
            OrphanBytes = orphanBytes,
        }) is { } openError)
        {
            // Nothing durable exists and nothing has been touched, so this is still a clean refusal.
            return Aborted(request, MirrorReconcileOutcome.AbortedBeforeDeleting, openError);
        }
        Log(request, $"opened: {request.Orphans.Count} orphan(s), {orphanBytes} byte(s), timing={request.Profile.Policies.MirrorDeletion}");

        // ---- the deletion loop -------------------------------------------------------------------
        int deleted = 0, skipped = 0, consecutiveJournalFailures = 0;
        long bytesDeleted = 0;
        List<MirrorDeletionFailure> failures = [];
        string? midPassAbort = null;

        foreach (RunDeleteItem orphan in request.Orphans)
        {
            if (ct.IsCancellationRequested)
            {
                midPassAbort = "the pass was cancelled";
                break;
            }
            // Re-checked every iteration, not once: a user who hits pause mid-pass means "stop", and
            // the next boundary is the soonest we can honour that without abandoning a half-done move.
            if (pauseState.IsPaused)
            {
                midPassAbort = "the engine was paused";
                break;
            }
            // A PER-RUN pause WAITS where the global pause above ABORTS, and the difference is deliberate.
            // The global pause is a safety brake on the whole engine, so refusing a destructive pass under
            // it is the conservative reading. A per-run pause is this user saying "hold this one" about a
            // run they already approved; answering that by silently abandoning its deletion half would
            // leave the destination not a mirror of the source, reported as an abort reason they never
            // asked for. Waiting between orphans holds no lock and no journal handle — DeleteOneAsync
            // acquires and releases per orphan — so a long pause here costs nothing but time.
            if (_runPause.IsRunPaused(request.RunId))
            {
                Log(request, "paused");
                while (_runPause.IsRunPaused(request.RunId) && !ct.IsCancellationRequested)
                    await Task.Delay(PausePollInterval, ct).ConfigureAwait(false);
                Log(request, "resumed");
                // Re-check the two gates above on the next iteration rather than falling through: the
                // engine may have been globally paused, or the run cancelled, while we waited.
                if (ct.IsCancellationRequested || pauseState.IsPaused)
                {
                    midPassAbort = ct.IsCancellationRequested
                        ? "the pass was cancelled"
                        : "the engine was paused";
                    break;
                }
            }

            OrphanDisposition disposition;
            try
            {
                disposition = await DeleteOneAsync(request, orphan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancel raised INSIDE the orphan — the lock await is the one place that can do it,
                // and its `when (!ct.IsCancellationRequested)` filter deliberately declines a real
                // cancel. Handled here rather than letting it unwind to DeleteAsync, whose catch
                // returns Deleted = 0: everything already recycled has to stay in these counters and
                // in the close record, which is this pass's only durable account of what it did.
                midPassAbort = "the pass was cancelled";
                break;
            }
            switch (disposition.Kind)
            {
                case OrphanDispositionKind.Deleted:
                    deleted++;
                    bytesDeleted += orphan.SizeBytes;
                    consecutiveJournalFailures = 0;
                    Log(request, $"deleted {orphan.Path}");
                    break;

                case OrphanDispositionKind.Skipped:
                    skipped++;
                    consecutiveJournalFailures = 0;
                    Log(request, $"skipped {orphan.Path}: {disposition.Reason}");
                    break;

                case OrphanDispositionKind.Failed:
                    skipped++;
                    failures.Add(new MirrorDeletionFailure(orphan.Path, disposition.Reason!));
                    consecutiveJournalFailures = 0;
                    Log(request, $"FAILED {orphan.Path}: {disposition.Reason}");
                    break;

                case OrphanDispositionKind.JournalFailed:
                    skipped++;
                    consecutiveJournalFailures++;
                    Log(request, $"skipped {orphan.Path}: {disposition.Reason}");
                    if (consecutiveJournalFailures >= config.MirrorMaxConsecutiveJournalFailures)
                    {
                        // The journal is effectively gone. Continuing would remove files with no
                        // durable record that we did — exactly the state the write-ahead exists to
                        // prevent.
                        midPassAbort =
                            $"the write-ahead journal failed {consecutiveJournalFailures} times in a row " +
                            $"({disposition.Reason})";
                    }
                    break;
            }
            if (midPassAbort is not null)
                break;
        }

        // ---- close -------------------------------------------------------------------------------
        MirrorReconcileOutcome outcome = midPassAbort is not null
            ? MirrorReconcileOutcome.AbortedMidPass
            : failures.Count > 0 || skipped > 0
                ? MirrorReconcileOutcome.PartiallyCompleted
                : MirrorReconcileOutcome.Completed;

        TryAppend(new MirrorReconcileClosedRecord
        {
            JobId = request.PassId.Value,
            Seq = 0,
            AtUtc = time.GetUtcNow(),
            Outcome = outcome,
            Deleted = deleted,
            Skipped = skipped,
            BytesDeleted = bytesDeleted,
            AbortReason = midPassAbort,
        });
        Log(request, $"closed {outcome} ({deleted} deleted, {skipped} skipped, {bytesDeleted} byte(s))");

        return new MirrorDeletionResult
        {
            PassId = request.PassId,
            Outcome = outcome,
            Deleted = deleted,
            Skipped = skipped,
            BytesDeleted = bytesDeleted,
            AbortReason = midPassAbort,
            Failures = failures,
        };
    }

    /// <summary>Removes one orphan, or explains why it did not.
    ///
    /// <para>Ordering is the whole point and is not negotiable: take the path lock, re-verify under it,
    /// journal the intent and get it to disk, and only then call the trash service. A crash between the
    /// journal append and the move leaves a <c>mrdel</c> with no <c>mrdone</c> — the one ambiguous
    /// state, and safely ambiguous, because the file is then either untouched or retrievable from the
    /// Recycle Bin.</para></summary>
    private async Task<OrphanDisposition> DeleteOneAsync(
        MirrorDeletionRequest request, RunDeleteItem orphan, CancellationToken ct)
    {
        Result<NormalizedPath, JobError> normalized = NormalizedPath.Create(orphan.Path);
        if (normalized.TryGetError(out JobError? pathError))
            return OrphanDisposition.Skip($"unusable path ({pathError.Message})");
        normalized.TryGetValue(out NormalizedPath path);

        // Defence in depth over the sweep's own exclusions. The sweep already skips infrastructure
        // directories and temp files, but it probes a composed span; re-checking the materialized path
        // costs nothing per orphan and closes the gap for any entry that reached us another way.
        if (InfrastructurePaths.IsInfrastructurePath(path.Value))
            return OrphanDisposition.Skip("the path is engine infrastructure");

        // A path a job is actively writing. The plan said it was an orphan; the registry says a
        // placement is in flight for it right now, which outranks a snapshot taken earlier.
        if (suppression.IsSuppressed(path))
            return OrphanDisposition.Skip("a job is currently writing this path");

        // Anything this run itself placed is off-limits, whatever the plan predicted. This is the
        // structural guard against plan drift: conflict resolution can legitimately choose a different
        // final path at execution time than the probe predicted at plan time, and without this such a
        // path would look like an orphan the run had just created.
        if (request.PathsWrittenByThisRun.Contains(path.Value))
            return OrphanDisposition.Skip("this run wrote to this path");

        // One path at a time: a single-element set is trivially in NormalizedPath order, so
        // I-LOCK-ORDER holds without sorting, and the lock is held for one move rather than for the
        // whole pass. AcquireAsync has no timeout of its own, hence the linked deadline — a path a live
        // job holds must be skipped, never waited on indefinitely.
        using CancellationTokenSource lockWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lockWait.CancelAfter(config.MirrorLockWaitTimeout);
        PathLockSet locks;
        try
        {
            locks = await lockRegistry.AcquireAsync([path], request.PassId, lockWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OrphanDisposition.Skip("another job holds this path");
        }

        await using (locks)
        {
            // Re-verify under the lock. The snapshot is a point-in-time plan and the user approved
            // THAT file; a different one now sitting at the path is not what was approved.
            FileInfo info = new(path.Value);
            if (!info.Exists)
                return OrphanDisposition.Skip("already gone");
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return OrphanDisposition.Skip("reparse point (symlink/junction)");
            if (!SameFile(info, orphan))
                return OrphanDisposition.Skip("modified since the run was planned");

            if (TryAppend(new MirrorOrphanTrashingRecord
            {
                JobId = request.PassId.Value,
                Seq = 0,
                AtUtc = time.GetUtcNow(),
                Path = path.Value,
                TargetRoot = orphan.TargetRoot,
                SizeBytes = orphan.SizeBytes,
            }) is { } journalError)
            {
                // Nothing removed: the write-ahead record is the precondition for removing anything.
                return OrphanDisposition.JournalFailure(journalError);
            }

            Result trashed = trash.MoveToTrash(path.Value);
            if (trashed.TryGetError(out string? trashError))
            {
                TryAppend(Trashed(request, path.Value, trashError));
                return OrphanDisposition.Fail(trashError);
            }

            Result recorded = audit.Append(new MirrorDeletionAuditRecord(
                request.PassId.Value, request.Profile.Id, request.RunId, path.Value, orphan.TargetRoot,
                orphan.SizeBytes, MirrorDeletionAuditLog.RecycleBinDestination, time.GetUtcNow()));
            if (recorded.TryGetError(out string? auditError))
            {
                // The file IS in the Recycle Bin and cannot be un-deleted, but the no-loss trail could
                // not be written — so this counts as a FAILED deletion and is reported as one. Calling
                // it a success because the move worked is how an audit trail quietly stops being
                // trustworthy.
                TryAppend(Trashed(request, path.Value, $"deleted, but its audit row failed: {auditError}"));
                return OrphanDisposition.Fail($"deleted, but its audit row could not be written: {auditError}");
            }

            TryAppend(Trashed(request, path.Value, error: null));
            return OrphanDisposition.Ok();
        }
    }

    // ---- gates ---------------------------------------------------------------------------------------

    /// <summary>Every reason to delete NOTHING, evaluated before the pass is journalled. Fail-closed by
    /// construction: each returns a sentence the user can act on, and any doubt about the completeness
    /// of the plan refuses the whole phase rather than acting on the part that looks certain.</summary>
    private string? Refuse(MirrorDeletionRequest request)
    {
        Profile profile = request.Profile;

        if (profile.SyncMode != SyncMode.Mirror)
            return "the profile is not a Mirror profile";

        // Zero sources would make EVERY destination file an orphan — an implicit "empty the target
        // tree" that must never happen by omission.
        if (profile.Sources.Count == 0)
            return "the profile has no sources, which would make every destination file an orphan";
        if (profile.Targets.Count == 0)
            return "the profile has no targets";

        // A narrowed run's plan covers only part of the source set, so everything outside the scope
        // reads as an orphan. Reconcile only from a whole-profile run.
        if (request.ScopePath is not null)
            return $"this run covered only \"{request.ScopePath}\", not the whole profile";

        // The plan is a prefix or was otherwise incomplete: a file that appears to have no source may
        // well be written by a source the scan never reached.
        if (request.PlanTruncated)
            return "the run's plan was incomplete, so its orphan list cannot be trusted";

        // Deliberately stricter than the dry run, which only marks a report Truncated for this. A
        // preview may be incomplete; a deletion may not.
        if (request.EnumerationIncomplete)
            return "part of the source or destination tree could not be read, so the orphan list may be wrong";

        // An explicit per-source existence check, because a source root that is missing raises no
        // enumeration fault of its own if the walk never reached it.
        foreach (SourceConfig source in profile.Sources)
            if (!Directory.Exists(source.Path))
                return $"source \"{source.Path}\" is missing or unreadable";

        if (request.CopyJobsFailed > 0)
            return request.CopyJobsFailed == 1
                ? "1 file could not be copied, so the destination is not yet a mirror of the source"
                : $"{request.CopyJobsFailed} files could not be copied, so the destination is not yet a mirror of the source";

        if (pauseState.IsPaused)
            return "the engine is paused";

        if (request.Orphans.Count > config.MirrorMaxOrphans)
            return $"the plan names {request.Orphans.Count} files to delete, above the {config.MirrorMaxOrphans} safety limit";

        return RefuseOnRatio(request);
    }

    /// <summary>Refuses a pass that would remove most of a target root.
    /// <para>The mistakes this catches — a <c>TargetLayout</c> flip, or a source share that remounted
    /// empty — produce a plan in which every existing destination file is legitimately, correctly
    /// classified as an orphan. Every other gate reports green. Only the shape of the result is
    /// suspicious, so only a proportion test can catch it.</para></summary>
    private string? RefuseOnRatio(MirrorDeletionRequest request)
    {
        Dictionary<string, int> orphansByRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (RunDeleteItem orphan in request.Orphans)
            orphansByRoot[orphan.TargetRoot] = orphansByRoot.GetValueOrDefault(orphan.TargetRoot) + 1;

        foreach ((string root, int orphans) in orphansByRoot)
        {
            if (orphans < config.MirrorRatioFloor)
                continue;
            if (!request.SweptFilesByTargetRoot.TryGetValue(root, out int swept) || swept <= 0)
                continue;
            double fraction = (double)orphans / swept;
            if (fraction > config.MirrorMaxDeleteFraction)
                return $"deleting {orphans} of the {swept} file(s) under \"{root}\" would remove "
                    + $"{fraction:P0} of it, above the {config.MirrorMaxDeleteFraction:P0} safety limit — "
                    + "check the profile's target layout and that every source is actually available";
        }
        return null;
    }

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>Whether the file on disk is still the one the plan named. Size and last-write only —
    /// the pass deliberately does not hash: an orphan is about to be recycled, not verified, and the
    /// question is "did this change since the user approved removing it", which a timestamp answers.
    /// A two-second tolerance absorbs filesystem timestamp granularity differences.</summary>
    private static bool SameFile(FileInfo info, RunDeleteItem planned) =>
        info.Length == planned.SizeBytes
        && (info.LastWriteTimeUtc - planned.LastWriteUtc.UtcDateTime).Duration() <= TimeSpan.FromSeconds(2);

    private static IEnumerable<string> TargetRoots(Profile profile)
    {
        foreach (TargetConfig target in profile.Targets)
            yield return target.Path;
    }

    private MirrorOrphanTrashedRecord Trashed(MirrorDeletionRequest request, string path, string? error) => new()
    {
        JobId = request.PassId.Value,
        Seq = 0,
        AtUtc = time.GetUtcNow(),
        Path = path,
        Error = error,
    };

    private MirrorDeletionResult Aborted(
        MirrorDeletionRequest request, MirrorReconcileOutcome outcome, string reason)
    {
        // Journalled only when the pass had already opened; an outright refusal writes no records at
        // all, which is what keeps "refused" and "started then stopped" distinguishable on disk.
        if (outcome == MirrorReconcileOutcome.AbortedMidPass)
        {
            TryAppend(new MirrorReconcileClosedRecord
            {
                JobId = request.PassId.Value,
                Seq = 0,
                AtUtc = time.GetUtcNow(),
                Outcome = outcome,
                Deleted = 0,
                Skipped = 0,
                BytesDeleted = 0,
                AbortReason = reason,
            });
        }
        Log(request, $"aborted: {reason}");
        return new MirrorDeletionResult
        {
            PassId = request.PassId,
            Outcome = outcome,
            AbortReason = reason,
        };
    }

    private string? TryAppend(JournalRecord record)
    {
        Result appended = journal.Append(record);
        if (appended.TryGetError(out string? error))
        {
            logger.LogError("Mirror deletion pass {PassId}: journal append failed: {Error}", record.JobId, error);
            return error;
        }
        return null;
    }

    private void Log(MirrorDeletionRequest request, string line) => jobLog.Append(request.PassId.Value, line);

    private enum OrphanDispositionKind { Deleted, Skipped, Failed, JournalFailed }

    private readonly record struct OrphanDisposition(OrphanDispositionKind Kind, string? Reason)
    {
        public static OrphanDisposition Ok() => new(OrphanDispositionKind.Deleted, null);
        public static OrphanDisposition Skip(string reason) => new(OrphanDispositionKind.Skipped, reason);
        public static OrphanDisposition Fail(string reason) => new(OrphanDispositionKind.Failed, reason);
        public static OrphanDisposition JournalFailure(string reason) => new(OrphanDispositionKind.JournalFailed, reason);
    }
}
