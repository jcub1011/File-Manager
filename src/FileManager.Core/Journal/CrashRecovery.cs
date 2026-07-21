using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Journal;

/// <summary>Spec §6.3 / §7.3 — at startup, resolves every OPEN journal entry to CLOSED before the
/// IPC server starts and any trigger fires (I-RECOVER-FIRST). Reconstructs per-target state from
/// the journal, probes the filesystem, then completes forward (table 2) or rolls back (via
/// <see cref="IRollbackExecutor"/>) per the classification tables. All state comes from the
/// <c>job-opened</c> record — recovery never depends on a live profile. Runs synchronously;
/// hashing blocks (there is no request load yet).</summary>
public sealed class CrashRecovery(
    IJobJournal journal,
    IFileHasher hasher,
    IRollbackExecutor rollback,
    ISourceDispositionService disposition,
    EnginePaths paths,
    EngineConfig config,
    TimeProvider time,
    ILogger<CrashRecovery> logger) : ICrashRecovery
{
    private const int CopyBufferSize = 1024 * 1024;
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    public Result<RecoveryReport, JobError> Recover(CancellationToken ct = default)
    {
        Result<IReadOnlyList<JournalRecord>, JobError> read = journal.ReadAll();
        if (read.TryGetError(out JobError? readError))
            return readError;
        read.TryGetValue(out IReadOnlyList<JournalRecord>? all);

        var byJob = all!.GroupBy(r => r.JobId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Seq).ToList());
        var knownJobs = new HashSet<Guid>(byJob.Keys);

        int recovered = 0, forward = 0, rolledBack = 0, cleaned = 0;
        var quarantined = new ConcurrentBag<string>();

        // Recover jobs concurrently — the one axis that is genuinely independent: each job owns a
        // distinct WorkspaceDir (job-scoped temp), and journal.Append is thread-safe (its own lock), so
        // the per-job work (which blocks on hashing and copies) overlaps safely. Shared state is
        // confined: counters are interlocked, quarantined is a concurrent bag, and journal.Rotate +
        // SweepOrphans run in the serial tail after the barrier. Cancellation propagates as before (OCE
        // out of Recover).
        //   Caveat: final *target* paths are NOT proven disjoint across jobs — two crashed jobs could in
        // principle have targeted the same destination. Parallel recovery assumes they did not; the
        // serial version made no ordering guarantee there either, so this is not a regression.
        //   Nesting: RecoverJob → rollback fans out per-target, but RollbackExecutor caps that fan-out
        // (MaxTargetParallelism) so the nested thread-pool demand stays O(cores × 8), not O(cores²).
        // Recovery also runs before the IPC server starts (I-RECOVER-FIRST), so it contends with nothing
        // for pool threads while it borrows them for this blocking work.
        Parallel.ForEach(
            byJob,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = ct },
            entry =>
            {
                Guid jobId = entry.Key;
                List<JournalRecord> records = entry.Value;
                if (records.OfType<JobClosedRecord>().Any())
                    return;                                     // already CLOSED
                if (records.OfType<JobOpenedRecord>().FirstOrDefault() is not { } opened)
                    return;                                     // records with no open (shouldn't happen)

                Interlocked.Increment(ref recovered);
                try
                {
                    RecoveryOutcome outcome = RecoverJob(jobId, opened, records, quarantined);
                    switch (outcome)
                    {
                        case RecoveryOutcome.CompletedForward: Interlocked.Increment(ref forward); break;
                        case RecoveryOutcome.RolledBack: Interlocked.Increment(ref rolledBack); break;
                        case RecoveryOutcome.CleanedPrePlacement: Interlocked.Increment(ref cleaned); break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Recovery of job {JobId} failed; leaving it for the next startup", jobId);
                    Interlocked.Decrement(ref recovered);   // not resolved
                }
            });

        // Compact: startup recovery closes every job, so this collapses to one fresh segment.
        journal.Rotate();

        var quarantinedPaths = quarantined.ToList();
        SweepOrphans(knownJobs, quarantinedPaths);

        var report = new RecoveryReport
        {
            JobsRecovered = recovered,
            CompletedForward = forward,
            RolledBack = rolledBack,
            CleanedPrePlacement = cleaned,
            QuarantinedPaths = quarantinedPaths,
        };
        if (recovered > 0 || quarantinedPaths.Count > 0)
            logger.LogInformation("Crash recovery: {Recovered} recovered ({Forward} forward, {Back} rolled back, {Clean} pre-placement), {Quarantined} quarantined",
                recovered, forward, rolledBack, cleaned, quarantinedPaths.Count);
        return report;
    }

    private enum RecoveryOutcome { CompletedForward, RolledBack, CleanedPrePlacement }

    private RecoveryOutcome RecoverJob(Guid jobId, JobOpenedRecord opened, List<JournalRecord> records, ConcurrentBag<string> quarantined)
    {
        bool sealedPresent = records.OfType<OutputSealedRecord>().Any();
        bool committed = records.OfType<JobCommittedRecord>().Any();
        bool rollbackBegun = records.OfType<RollbackBeginRecord>().Any();
        OutputSealedRecord? seal = records.OfType<OutputSealedRecord>().FirstOrDefault();

        TargetRecovery[] targets = ReconstructTargets(opened, records);

        // Row I — post-commit: placement is complete by definition; re-attempt disposition idempotently.
        if (committed)
        {
            ReattemptDisposition(jobId, opened, targets);
            return RecoveryOutcome.CompletedForward;
        }

        // Row J — crashed rollback: resume the sweep for targets lacking a target-rolledback record.
        if (rollbackBegun)
        {
            RollBack(jobId, opened, seal, targets.Where(t => !t.RolledBack).ToArray(), "recovery: resume crashed rollback");
            return RecoveryOutcome.RolledBack;
        }

        // Rows A / B — pre-placement: no staged/placed artifacts hold user data yet. Cleanest safe
        // outcome is a rollback sweep (deletes temps + workspace) and close Failed. Re-delivery is
        // idempotent (spec §3.4.1).
        bool anyStagedOrPlaced = targets.Any(t => t.State is TargetState.Staged or TargetState.Placed);
        if (!sealedPresent || !anyStagedOrPlaced)
        {
            RollBack(jobId, opened, seal, targets, "recovery: pre-placement cleanup");
            return RecoveryOutcome.CleanedPrePlacement;
        }

        // Rows E–H — mid-placement: forward-completion gate (§7.3).
        if (ForwardCompletionAllowed(opened, seal, targets))
        {
            CompleteForward(jobId, opened, seal!, targets, quarantined);
            return RecoveryOutcome.CompletedForward;
        }

        RollBack(jobId, opened, seal, targets, "recovery: mid-placement rollback (forward gate failed)");
        return RecoveryOutcome.RolledBack;
    }

    /// <summary>Complete forward iff (a) output-sealed exists, (b) the workspace output is present
    /// with matching size+hash OR every unplaced target has a temp whose hash matches, and
    /// (c) Verification is a hash-based method (under None/SizeTimestamp there is no reference hash,
    /// so mid-placement always rolls back — conservative). Re-hashing uses the same method that
    /// produced the seal (opened.Policies.Verification).</summary>
    private bool ForwardCompletionAllowed(JobOpenedRecord opened, OutputSealedRecord? seal, TargetRecovery[] targets)
    {
        VerificationMethod method = opened.Policies.Verification;
        if (seal is null || method is VerificationMethod.None or VerificationMethod.SizeTimestamp)
            return false;

        // Decide BEFORE CompleteForward mutates anything: every not-yet-terminal target must be
        // safely completable, or we roll back the whole job (source preserved, re-delivery idempotent
        // per §3.4.1) rather than commit + dispose over a target we cannot honour.
        foreach (TargetRecovery t in targets)
        {
            if (t.State is TargetState.SatisfiedUnchanged or TargetState.SkippedConflict)
                continue;

            if (t.State == TargetState.Placed)
            {
                // I-DISPOSE: a placed target whose file is now missing or hash-mismatched cannot be
                // trusted; refuse forward completion so the source is never disposed over it.
                if (t.FinalPath is null
                    || !File.Exists(t.FinalPath)
                    || !HashEquals(t.FinalPath, seal.ContentHash, method))
                {
                    logger.LogError(
                        "Recovery: placed target {Index} (\"{Final}\") missing or hash-mismatched after crash; refusing forward completion",
                        t.TargetIndex, t.FinalPath);
                    return false;
                }
                continue;
            }

            // A lost target-write-begin (e.g. mid-journal corruption) leaves these null. Completing
            // forward would silently skip the target, then commit and dispose the source — total loss.
            if (t.FinalPath is null || t.TempPath is null)
            {
                logger.LogError(
                    "Recovery: target {Index} has no target-write-begin paths; refusing forward completion",
                    t.TargetIndex);
                return false;
            }
        }

        if (File.Exists(seal.OutputPath)
            && new FileInfo(seal.OutputPath).Length == seal.SizeBytes
            && HashEquals(seal.OutputPath, seal.ContentHash, method))
            return true;

        // Workspace gone — forward only if every not-yet-placed target already has a good temp.
        foreach (TargetRecovery t in targets)
        {
            if (t.State is TargetState.Placed or TargetState.SatisfiedUnchanged or TargetState.SkippedConflict)
                continue;
            if (!File.Exists(t.TempPath!) || !HashEquals(t.TempPath!, seal.ContentHash, method))
                return false;
        }
        return true;
    }

    private void CompleteForward(Guid jobId, JobOpenedRecord opened, OutputSealedRecord seal, TargetRecovery[] targets, ConcurrentBag<string> quarantined)
    {
        // The forward gate (ForwardCompletionAllowed) has already verified that every placed target
        // is intact and every not-yet-placed target has resolvable paths, so placement below cannot
        // silently skip a target.
        foreach (TargetRecovery t in targets)
        {
            if (t.State is TargetState.Placed or TargetState.SatisfiedUnchanged or TargetState.SkippedConflict)
                continue;

            ForwardPlaceTarget(jobId, opened, seal, t, quarantined);
        }

        // Commit point (I-DISPOSE): job-committed MUST be durable before the source is disposed.
        // If the append fails, throwing leaves the job OPEN with every placed copy intact — the next
        // startup re-runs the gate (all placed targets now hash-match) and retries the commit.
        // Disposing on a failed append would let a later rollback delete the copies of an
        // already-deleted source: total loss.
        if (!Journal(new JobCommittedRecord { JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow() }))
            throw new InvalidOperationException(
                $"could not journal job-committed for job {jobId}; leaving the job open (source not disposed)");

        ReattemptDisposition(jobId, opened, targets, alreadyCommitted: true);
    }

    private void ForwardPlaceTarget(Guid jobId, JobOpenedRecord opened, OutputSealedRecord seal, TargetRecovery t, ConcurrentBag<string> quarantined)
    {
        if (t.FinalPath is null || t.TempPath is null)
            // Unreachable: the forward-completion gate rejects any job with such a target. Throwing
            // (rather than silently returning) guarantees a target is never skipped on the way to a
            // commit+dispose — the RecoverJob catch leaves the job for the next startup, source intact.
            throw new InvalidOperationException(
                $"forward-place target {t.TargetIndex} of job {jobId} has no target-write-begin paths");

        // Ensure a verified temp exists (rows C1/C2/D3): re-copy from the workspace if the temp is
        // absent or its hash does not match the reference.
        if (!File.Exists(t.TempPath) || !HashEquals(t.TempPath, seal.ContentHash, opened.Policies.Verification))
            CopyFlush(seal.OutputPath, t.TempPath);

        // Place (rows D1/D2/E/F).
        if (!File.Exists(t.FinalPath))
        {
            File.Move(t.TempPath, t.FinalPath, overwrite: false);
        }
        else if (HashEquals(t.FinalPath, seal.ContentHash, opened.Policies.Verification))
        {
            // F/D2: the rename already happened; the target-placed record was lost. Drop the temp.
            SafeDelete(t.TempPath);
        }
        else if (opened.Policies.OverwriteHandling == OverwriteHandling.StageOverwrites && t.FinalExisted)
        {
            string staged = StagedPath(opened, jobId, t);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            Replace(t.TempPath, t.FinalPath, staged);
        }
        else if (opened.Policies.OverwriteHandling == OverwriteHandling.DirectOverwrite && t.FinalExisted)
        {
            // The job's own pre-crash intent was to clobber this name (the live path does the same
            // atomic replace), so completing that overwrite honours the journaled plan.
            File.Move(t.TempPath, t.FinalPath, overwrite: true);
        }
        else
        {
            // External interference: the job recorded FinalExisted=false, yet a file with foreign
            // content now sits at the final name (it appeared between the crash and this startup).
            // The live path refuses to clobber it (AtomicPlacer places fresh files overwrite:false);
            // recovery must not either — quarantine it, then place. If the quarantine move fails,
            // the throw leaves the job for the next startup, nothing destroyed.
            string quarantinePath = QuarantinePath(jobId, t);
            Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
            File.Move(t.FinalPath, quarantinePath, overwrite: false);
            quarantined.Add(quarantinePath);
            logger.LogWarning(
                "Recovery: external file appeared at \"{Final}\" after the crash of job {JobId}; quarantined it to \"{Quarantine}\" before placing",
                t.FinalPath, jobId, quarantinePath);
            File.Move(t.TempPath, t.FinalPath, overwrite: false);
        }

        // Best-effort by design: if this append fails, the worst case is a re-place of an identical,
        // hash-verified file on the next startup (idempotent) — never data loss.
        Journal(new TargetPlacedRecord { JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(), TargetIndex = t.TargetIndex });
    }

    /// <summary>A collision-free spot under the engine's quarantine root for a foreign file found at
    /// a final path recovery needs to place into. Keyed by job + target; a leftover from an earlier
    /// interrupted recovery of the same job gets a random disambiguator rather than a failure.</summary>
    private string QuarantinePath(Guid jobId, TargetRecovery t)
    {
        string candidate = Path.Combine(paths.QuarantineDirectory, jobId.ToString("N"), $"{t.TargetIndex}-{Path.GetFileName(t.FinalPath!)}");
        if (File.Exists(candidate))
            candidate = $"{candidate}.{Guid.NewGuid():N}";
        return candidate;
    }

    private void ReattemptDisposition(Guid jobId, JobOpenedRecord opened, TargetRecovery[] targets, bool alreadyCommitted = false)
    {
        bool anySkipped = targets.Any(t => t.State == TargetState.SkippedConflict);
        Result<DispositionAuditRecord, JobError> disposed = disposition.Dispose(jobId, opened.Source, opened.Policies, anySkipped);
        string? dispositionError = disposed.TryGetError(out JobError? de) ? de.Message : null;
        if (dispositionError is not null)
            logger.LogWarning("Recovery disposition failed for job {JobId}: {Error} (copies are safe)", jobId, dispositionError);

        Journal(new JobClosedRecord
        {
            JobId = jobId, Seq = 0, AtUtc = time.GetUtcNow(),
            Outcome = JobOutcome.Succeeded, DispositionError = dispositionError,
        });
    }

    private void RollBack(Guid jobId, JobOpenedRecord opened, OutputSealedRecord? seal, TargetRecovery[] targets, string reason)
    {
        var items = targets.Select(t => new TargetRollbackItem
        {
            TargetIndex = t.TargetIndex,
            State = t.State,
            TempPath = t.TempPath,
            FinalPath = t.FinalPath,
            StagedPath = t.StagedPath ?? (t.FinalPath is not null ? StagedPath(opened, jobId, t) : null),
            FinalExistedBeforeJob = t.FinalExisted,
        }).ToList();

        var context = new RollbackContext
        {
            JobId = new JobId(jobId),
            Cause = new JobError { Code = JobErrorCode.RollbackIncomplete, Message = reason },
            Targets = items,
            WorkspaceDir = opened.WorkspaceDir,
            OverwriteHandling = opened.Policies.OverwriteHandling,
            // The rollback hash gate: lets the executor recognize a placed file that was modified
            // externally between the crash and this startup, and leave it rather than destroy it.
            ExpectedContentHash = seal?.ContentHash,
            Verification = opened.Policies.Verification,
        };
        rollback.Rollback(context);
    }

    private TargetRecovery[] ReconstructTargets(JobOpenedRecord opened, List<JournalRecord> records)
    {
        var targets = new TargetRecovery[opened.Targets.Count];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = new TargetRecovery { TargetIndex = i, State = TargetState.Pending };

        foreach (JournalRecord r in records)
        {
            switch (r)
            {
                case TargetWriteBeginRecord twb when InRange(twb.TargetIndex, targets):
                    targets[twb.TargetIndex].TempPath = twb.TempPath;
                    targets[twb.TargetIndex].FinalPath = twb.FinalPath;
                    targets[twb.TargetIndex].FinalExisted = twb.FinalExisted;
                    Promote(targets[twb.TargetIndex], TargetState.TempWriting);
                    break;
                case TargetVerifiedRecord tv when InRange(tv.TargetIndex, targets):
                    Promote(targets[tv.TargetIndex], TargetState.Verified);
                    break;
                case TargetStagedRecord ts when InRange(ts.TargetIndex, targets):
                    targets[ts.TargetIndex].StagedPath = ts.StagedPath;
                    Promote(targets[ts.TargetIndex], TargetState.Staged);
                    break;
                case TargetPlacedRecord tp when InRange(tp.TargetIndex, targets):
                    Promote(targets[tp.TargetIndex], TargetState.Placed);
                    break;
                case TargetUnchangedRecord tu when InRange(tu.TargetIndex, targets):
                    targets[tu.TargetIndex].FinalPath = tu.FinalPath;
                    targets[tu.TargetIndex].State = TargetState.SatisfiedUnchanged;
                    break;
                case TargetSkippedRecord tk when InRange(tk.TargetIndex, targets):
                    targets[tk.TargetIndex].State = TargetState.SkippedConflict;
                    break;
                case TargetRolledBackRecord tr when InRange(tr.TargetIndex, targets):
                    targets[tr.TargetIndex].RolledBack = true;
                    targets[tr.TargetIndex].State = tr.Error is null ? TargetState.RolledBack : TargetState.RollbackFailed;
                    break;
            }
        }
        return targets;
    }

    // Only advance the state along the forward chain; never regress (records are seq-ordered anyway).
    private static void Promote(TargetRecovery t, TargetState to)
    {
        if ((int)to > (int)t.State || t.State == TargetState.Pending)
            t.State = to;
    }

    private static bool InRange(int index, TargetRecovery[] targets) => index >= 0 && index < targets.Length;

    private static string StagedPath(JobOpenedRecord opened, Guid jobId, TargetRecovery t)
    {
        TargetPlan plan = opened.Targets[t.TargetIndex];
        string finalName = Path.GetFileName(t.FinalPath ?? plan.ProspectiveFinalPath);
        return Files.InfrastructurePaths.StagedPathFor(plan.TargetRoot, jobId, t.TargetIndex, finalName);
    }

    private void SweepOrphans(HashSet<Guid> knownJobs, List<string> quarantined)
    {
        string pipelineTmp = Path.Combine(config.TempRoot ?? paths.WorkDirectory, ".pipeline_tmp");
        if (!Directory.Exists(pipelineTmp))
            return;

        try
        {
            DateTimeOffset cutoff = time.GetUtcNow() - OrphanAge;
            foreach (string dir in Directory.EnumerateDirectories(pipelineTmp))
            {
                string name = Path.GetFileName(dir);
                bool tracked = Guid.TryParseExact(name, "N", out Guid id) && knownJobs.Contains(id);
                if (tracked)
                    continue;
                // No journal trace + older than 24h → safe to delete (no user data lives in a workspace).
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff.UtcDateTime)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "Could not delete orphaned workspace {Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Catch-all (directive): the enumeration itself (or GetLastWriteTimeUtc) can throw, and
            // orphan sweeping is best-effort cleanup — a failure here must not abort a recovery pass
            // whose jobs were already resolved above.
            logger.LogWarning(ex, "Orphan workspace sweep failed; skipping (best-effort)");
        }
        // Note (v1 substrate): .fm_staging orphans live under arbitrary target roots that recovery
        // cannot enumerate without the profile catalog; a staging dir left by a failed rollback is
        // quarantined by RollbackExecutor's I-STAGING-KEEP guard, not swept centrally here.
    }

    // --- probing / IO helpers (synchronous — recovery runs before any load) ---

    private bool HashEquals(string path, string expectedHash, VerificationMethod method)
    {
        Result<string, JobError> hashed = hasher.HashFileAsync(path, method).GetAwaiter().GetResult();
        return hashed.TryGetValue(out string? actual) && string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFlush(string source, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using FileStream src = new(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan);
        using FileStream dst = new(dest, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);
        src.CopyTo(dst, CopyBufferSize);
        dst.Flush(flushToDisk: true);
    }

    private void Replace(string temp, string final, string staged)
    {
        try { File.Replace(temp, final, staged, ignoreMetadataErrors: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning(ex, "File.Replace rejected for \"{Final}\" during recovery; falling back to two-step move", final);
            File.Move(final, staged);
            File.Move(temp, final);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): log before the throw reaches RecoverJob's
            // leave-for-next-startup handler, so the specific replace that failed is on record.
            logger.LogError(ex, "Staged replace of \"{Final}\" failed unexpectedly during recovery", final);
            throw;
        }
    }

    private void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort by contract (a leftover temp is harmless and infra-excluded from scans),
            // but never silent (directive).
            logger.LogDebug(ex, "Best-effort delete of \"{Path}\" failed", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort delete of \"{Path}\" failed unexpectedly", path);
        }
    }

    /// <summary>Appends and reports success. Callers decide per record whether a failed append is
    /// fatal (job-committed — disposing without it risks total loss) or best-effort (target-placed /
    /// job-closed — replay is idempotent, the next startup re-resolves the job).</summary>
    private bool Journal(JournalRecord record)
    {
        Result result = journal.Append(record);
        if (result.TryGetError(out string? error))
        {
            logger.LogError("Recovery journal append failed ({Type}): {Error}", record.GetType().Name, error);
            return false;
        }
        return true;
    }

    private sealed class TargetRecovery
    {
        public required int TargetIndex { get; init; }
        public TargetState State { get; set; }
        public string? TempPath { get; set; }
        public string? FinalPath { get; set; }
        public string? StagedPath { get; set; }
        public bool FinalExisted { get; set; }
        public bool RolledBack { get; set; }
    }
}
