using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Placement;
using FileManager.Core.Preflight;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Jobs;

/// <summary>Runs one Job through the §4.3 phase algorithm (this slice omits the transform phase —
/// Set 4), interleaving journal / lock / placement calls per the §7.2 write-ahead protocol and
/// driving <see cref="JobStateMachine"/> for job-level states (per-target state lives in the mutable
/// <see cref="TargetProgress"/> mirror the placer maintains). Never throws for a job failure — every
/// outcome, including <see cref="JobOutcome.RollbackFailed"/>, is returned as a
/// <see cref="JobCompletion"/>. The executor writes <c>job-opened</c>, <c>output-sealed</c>,
/// <c>target-skipped</c>, <c>job-committed</c>, and the terminal <c>job-closed</c> on the
/// non-rollback paths; on any post-<c>Opened</c> failure it hands off to
/// <see cref="IRollbackExecutor"/>, which owns the rollback sweep and the terminal close.</summary>
public sealed class JobExecutor(
    IJobJournal journal,
    PathLockRegistry lockRegistry,
    IDiskPreflight preflight,
    IFilterCompiler filterCompiler,
    IFileHasher hasher,
    IConflictResolver conflictResolver,
    IAtomicPlacer placer,
    IRollbackExecutor rollback,
    ISourceDispositionService disposition,
    IJobLogStore jobLog,
    TimeProvider time,
    ILogger<JobExecutor> logger) : IJobExecutor
{
    public async Task<JobCompletion> ExecuteAsync(JobPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long start = time.GetTimestamp();

        // Transformers are unsupported in this slice (Set 4). Refuse before Open so nothing is
        // journalled — the UI never emits transformers; only hand-edited JSON could reach here.
        if (plan.Profile.Transformers is { Count: > 0 })
        {
            logger.LogWarning("Job {JobId}: profile has transformers, which are not supported in this build", plan.JobId.Short);
            return Completed(plan, JobOutcome.Failed, null,
                new JobError { Code = JobErrorCode.TransformerFailed, Message = "transformers are not supported in this build" }, start);
        }

        JobExecution execution = new() { Plan = plan };
        try
        {
            return await RunAsync(execution, start, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Reaching here means cancellation before Open (post-Open cancellation routes through
            // rollback inside RunAsync). Nothing durable exists; report a skip.
            logger.LogInformation("Job {JobId} canceled before it opened", plan.JobId.Short);
            return Completed(plan, JobOutcome.Skipped, SkipReason.SourceDisposed, null, start);
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: the executor must never throw for a job failure.
            logger.LogError(ex, "Job {JobId} failed unexpectedly", plan.JobId.Short);
            return Completed(plan, JobOutcome.Failed, null,
                new JobError { Code = JobErrorCode.PlacementFailed, Message = ex.Message }, start);
        }
    }

    private async Task<JobCompletion> RunAsync(JobExecution execution, long start, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        JobStateMachine states = execution.States;

        // 1. LOCK — source + every prospective final path (§4.3 step 1). Archive-destination locking
        //    is a documented Set-3 simplification (disposition is post-commit, never rolled back).
        IReadOnlyList<NormalizedPath> lockPaths = BuildLockSet(plan, out JobError? lockError);
        if (lockError is not null)
            return Completed(plan, JobOutcome.Failed, null, lockError, start);

        await using PathLockSet locks = await lockRegistry.AcquireAsync(lockPaths, plan.JobId, ct).ConfigureAwait(false);
        states.Transition(JobState.Locked);

        // Re-check the source under the lock (overlap rule, spec §5.4). Gone → skip, NO journal record.
        if (!File.Exists(plan.Source.Path))
        {
            logger.LogInformation("Job {JobId}: source {Path} gone under lock; skipping", plan.JobId.Short, plan.Source.Path);
            return Completed(plan, JobOutcome.Skipped, SkipReason.SourceDisposed, null, start);
        }

        // 2. OPEN — the job now exists durably; from here a failure routes through rollback.
        JobError? open = TryAppend(new JobOpenedRecord
        {
            JobId = plan.JobId.Value,
            Seq = 0,
            AtUtc = time.GetUtcNow(),
            ProfileId = plan.ProfileId,
            Source = plan.Source,
            Policies = plan.Policies,
            WorkspaceDir = plan.WorkspaceDir,
            Targets = plan.Targets,
        });
        if (open is not null)
            return Completed(plan, JobOutcome.Failed, null, open, start);   // nothing written — no rollback needed
        states.Transition(JobState.Opened);
        Log(plan, "opened");

        // 3. PREFLIGHT — self-path check + free-space. I-WAL: nothing written, so close directly.
        JobError? preflightError = Preflight(plan);
        if (preflightError is not null)
        {
            states.Transition(JobState.Closed);
            TryAppend(Close(plan, JobOutcome.Failed));
            Log(plan, $"failed at preflight: {preflightError.Message}");
            return Completed(plan, JobOutcome.Failed, null, preflightError, start);
        }
        states.Transition(JobState.Preflighted);

        // 4. SCREEN — the authoritative filter gate (a manual single-file run is not pre-filtered).
        Result<bool, JobError> screen = Screen(plan, out string? decidingRule);
        if (screen.TryGetError(out JobError? screenError))
        {
            states.Transition(JobState.Closed);
            TryAppend(Close(plan, JobOutcome.Failed));
            Log(plan, $"failed at screen: {screenError.Message}");
            return Completed(plan, JobOutcome.Failed, null, screenError, start);
        }
        screen.TryGetValue(out bool included);
        if (!included)
        {
            states.Transition(JobState.Closed);
            TryAppend(Close(plan, JobOutcome.Skipped, SkipReason.Filtered));
            Log(plan, $"skipped (filtered: {decidingRule})");
            return Completed(plan, JobOutcome.Skipped, SkipReason.Filtered, null, start);
        }
        states.Transition(JobState.Screened);

        // 5. SEAL SOURCE AS OUTPUT — no transform: the source itself is the sealed artifact. The
        //    guard-only Transforming transition keeps the §7.1 sequence; no workspace is created.
        states.Transition(JobState.Transforming);
        Result<SealedOutput, JobError> sealResult = await SealSourceAsync(plan, ct).ConfigureAwait(false);
        if (sealResult.IsCanceled)
            return await RollBackAsync(execution, CanceledSeal(), start, ct).ConfigureAwait(false);
        if (sealResult.TryGetError(out JobError? sealError))
            return await RollBackAsync(execution, sealError, start, ct).ConfigureAwait(false);
        sealResult.TryGetValue(out SealedOutput? output);
        execution.Output = output;

        JobError? sealedError = TryAppend(new OutputSealedRecord
        {
            JobId = plan.JobId.Value,
            Seq = 0,
            AtUtc = time.GetUtcNow(),
            OutputPath = output!.Path,
            SizeBytes = output.SizeBytes,
            ContentHash = output.ContentHash,
        });
        if (sealedError is not null)
            return await RollBackAsync(execution, sealedError, start, ct).ConfigureAwait(false);
        states.Transition(JobState.OutputSealed);

        // 6. DISTRIBUTE + VERIFY + PLACE — bounded-parallel per target; cancel siblings on first fail.
        states.Transition(JobState.Distributing);
        JobError? distributeError = await DistributeAsync(execution, locks, ct).ConfigureAwait(false);
        if (distributeError is not null)
            return await RollBackAsync(execution, distributeError, start, ct).ConfigureAwait(false);

        // 7. COMMIT / SKIP decision.
        bool anyPlaced = false, anySkippedConflict = false, allUnchanged = true;
        foreach (TargetProgress tp in execution.Targets)
        {
            switch (tp.State)
            {
                case TargetState.Placed: anyPlaced = true; allUnchanged = false; break;
                case TargetState.SkippedConflict: anySkippedConflict = true; allUnchanged = false; break;
                case TargetState.SatisfiedUnchanged: break;
                default: allUnchanged = false; break;
            }
        }

        if (!anyPlaced && !anySkippedConflict && allUnchanged)
        {
            // Every target already held identical content: close Skipped WITHOUT committing or
            // disposing (spec §3.4.1 / flow §6.2 — re-delivery is idempotent). Committed is a
            // guard-only transition here; no JobCommittedRecord is written.
            states.Transition(JobState.Committed);
            states.Transition(JobState.Closed);
            TryAppend(Close(plan, JobOutcome.Skipped, SkipReason.UnchangedAtAllTargets));
            Log(plan, "skipped (unchanged at all targets)");
            return Completed(plan, JobOutcome.Skipped, SkipReason.UnchangedAtAllTargets, null, start);
        }

        // Commit point (I-DISPOSE): source disposition is authorized by this record and nothing else.
        // A failed commit append is fatal → rollback.
        JobError? commit = TryAppend(new JobCommittedRecord { JobId = plan.JobId.Value, Seq = 0, AtUtc = time.GetUtcNow() });
        if (commit is not null)
            return await RollBackAsync(execution, commit, start, ct).ConfigureAwait(false);
        states.Transition(JobState.Committed);
        Log(plan, "committed");

        // 8. DISPOSE — apply OnSuccess; a disposition failure is logged in job-closed, never rolled back.
        states.Transition(JobState.Disposing);
        string? dispositionError = ApplyDisposition(execution);
        states.Transition(JobState.Closed);
        TryAppend(Close(plan, JobOutcome.Succeeded, skipReason: null, dispositionError));
        Log(plan, dispositionError is null ? "succeeded" : $"succeeded (disposition error: {dispositionError})");
        return Completed(plan, JobOutcome.Succeeded, null, null, start);
    }

    // ---- phases ----------------------------------------------------------------------------------

    private static IReadOnlyList<NormalizedPath> BuildLockSet(JobPlan plan, out JobError? error)
    {
        List<NormalizedPath> paths = new(plan.Targets.Count + 1);
        Result<NormalizedPath, JobError> src = NormalizedPath.Create(plan.Source.Path);
        if (src.TryGetError(out JobError? srcError)) { error = srcError; return []; }
        src.TryGetValue(out NormalizedPath sourcePath);
        paths.Add(sourcePath);

        foreach (TargetPlan target in plan.Targets)
        {
            Result<NormalizedPath, JobError> finalPath = NormalizedPath.Create(target.ProspectiveFinalPath);
            if (finalPath.TryGetError(out JobError? finalError)) { error = finalError; return []; }
            finalPath.TryGetValue(out NormalizedPath fp);
            paths.Add(fp);
        }
        error = null;
        return paths;
    }

    private JobError? Preflight(JobPlan plan)
    {
        // Resolved-target self-path check (hard error, spec §3.2.3) — distinct from the profile-level
        // validator check, which cannot see the per-job resolved final paths.
        if (NormalizedPath.Create(plan.Source.Path).TryGetValue(out NormalizedPath source))
        {
            foreach (TargetPlan target in plan.Targets)
            {
                if (NormalizedPath.Create(target.ProspectiveFinalPath).TryGetValue(out NormalizedPath fp) && fp == source)
                    return new JobError
                    {
                        Code = JobErrorCode.SelfPathTarget,
                        Message = $"target resolves to the source path: {source.Value}",
                        Path = source.Value,
                        TargetIndex = target.TargetIndex,
                    };
            }
        }

        Result<DiskPreflightReport, JobError> report = preflight.Evaluate(plan);
        if (report.TryGetError(out JobError? evalError))
            return evalError;
        report.TryGetValue(out DiskPreflightReport? volumes);
        foreach (VolumeEstimate volume in volumes!.Volumes)
        {
            if (!volume.Sufficient)
                return new JobError
                {
                    Code = JobErrorCode.InsufficientDiskSpace,
                    Message = $"insufficient space on {volume.VolumeRoot}: need {volume.RequiredBytes + volume.SafetyMarginBytes} bytes, {volume.AvailableBytes} available",
                    Path = volume.VolumeRoot,
                };
        }
        return null;
    }

    private Result<bool, JobError> Screen(JobPlan plan, out string? decidingRule)
    {
        decidingRule = null;
        SourceConfig? matchedSource = FindMatchedSource(plan);
        Result<CompiledFilterSet, string> compiled = filterCompiler.Compile(plan.Profile.Filters, matchedSource?.Filters);
        if (compiled.TryGetError(out string? compileError))
            // A saved profile always compiles (validated at save); a failure here is corruption.
            // SourceUnreadable is the closest code — the file could not be evaluated.
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = $"could not compile filters: {compileError}", Path = plan.Source.Path };
        compiled.TryGetValue(out CompiledFilterSet? set);

        string relativePath = Path.GetRelativePath(plan.Payload.SourceRoot, plan.Source.Path);
        int depth = SeparatorCount(relativePath);
        string? normalized = set!.HasPatternRules ? NormalizeSeparators(relativePath) : null;
        FilterInput input = new(plan.Source.Path, relativePath, depth, plan.SourceMetadata, normalized);
        FilterDecision decision = set.Evaluate(in input);
        decidingRule = decision.DecidingRule;
        return decision.Matched;
    }

    private async Task<Result<SealedOutput, JobError>> SealSourceAsync(JobPlan plan, CancellationToken ct)
    {
        string hash = "";
        if (plan.Policies.Verification is VerificationMethod.Sha256 or VerificationMethod.XxHash128)
        {
            Result<string, JobError> hashed = await hasher.HashFileAsync(plan.Source.Path, plan.Policies.Verification, ct).ConfigureAwait(false);
            if (hashed.IsCanceled)
                return Result<SealedOutput, JobError>.Canceled();
            if (hashed.TryGetError(out JobError? hashError))
                return hashError;
            hashed.TryGetValue(out hash!);
        }
        return new SealedOutput
        {
            Path = plan.Source.Path,
            SizeBytes = plan.Source.SizeBytes,
            ContentHash = hash,
            SourceLastWriteUtc = plan.Source.LastWriteUtc,
        };
    }

    private async Task<JobError?> DistributeAsync(JobExecution execution, PathLockSet locks, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        JobError?[] errors = new JobError?[plan.Targets.Count];
        Task[] tasks = new Task[plan.Targets.Count];

        for (int i = 0; i < plan.Targets.Count; i++)
        {
            int index = i;
            tasks[index] = Task.Run(async () =>
            {
                JobError? error = await PlaceOneAsync(execution, locks, index, linked.Token).ConfigureAwait(false);
                if (error is not null)
                {
                    errors[index] = error;
                    linked.Cancel();   // cancel sibling target tasks (§4.3 step 6)
                }
            }, CancellationToken.None);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (JobError? error in errors)   // report the lowest-index failure
            if (error is not null)
                return error;
        return null;
    }

    private async Task<JobError?> PlaceOneAsync(JobExecution execution, PathLockSet locks, int index, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        TargetPlan target = plan.Targets[index];
        SealedOutput output = execution.Output!;
        try
        {
            // Unchanged short-circuit FIRST (spec §3.4.1, before conflict resolution). On Unchanged
            // the placer journals target-unchanged and sets TargetProgress.State itself.
            Result<UnchangedCheckResult, JobError> unchanged =
                await placer.CheckUnchangedAsync(execution, index, target.ProspectiveFinalPath, ct).ConfigureAwait(false);
            if (unchanged.IsCanceled)
                return null;   // a sibling failed / shutdown — not this target's error
            if (unchanged.TryGetError(out JobError? unchangedError))
                return unchangedError;
            unchanged.TryGetValue(out UnchangedCheckResult check);
            if (check == UnchangedCheckResult.Unchanged)
            {
                Log(plan, $"target {index}: unchanged, already satisfied");
                return null;
            }

            // Conflict resolution (single source → priority index 0).
            Result<ConflictOutcome, JobError> resolve = conflictResolver.Resolve(
                target.ProspectiveFinalPath, plan.Policies.ConflictResolution, output, sourceIndex: 0, plan.ProfileId, locks);
            if (resolve.TryGetError(out JobError? resolveError))
                return resolveError;
            resolve.TryGetValue(out ConflictOutcome? outcome);

            if (outcome!.Action == ConflictAction.SkipExistingKept)
            {
                JobError? skipped = TryAppend(new TargetSkippedRecord
                {
                    JobId = plan.JobId.Value,
                    Seq = 0,
                    AtUtc = time.GetUtcNow(),
                    TargetIndex = index,
                });
                if (skipped is not null)
                    return skipped;
                TargetProgress progress = execution.Targets[index];
                progress.State = TargetState.SkippedConflict;
                progress.FinalPath = outcome.FinalPath;
                Log(plan, $"target {index}: conflict — kept existing file");
                return null;
            }

            bool finalExists = File.Exists(outcome.FinalPath);
            Result<PlacementResult, JobError> placed = await placer.PlaceTargetAsync(new PlacementRequest
            {
                Execution = execution,
                TargetIndex = index,
                Output = output,
                FinalPath = outcome.FinalPath,
                FinalExists = finalExists,
                OverwriteHandling = plan.Policies.OverwriteHandling,
                Verification = plan.Policies.Verification,
            }, ct).ConfigureAwait(false);
            if (placed.IsCanceled)
                return null;
            if (placed.TryGetError(out JobError? placeError))
                return placeError;
            Log(plan, $"target {index}: placed at {outcome.FinalPath}");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;   // sibling failure / shutdown — the failing target reports the real cause
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: an unexpected throw becomes this target's placement error.
            logger.LogError(ex, "Job {JobId} target {Index} placement threw", plan.JobId.Short, index);
            return new JobError { Code = JobErrorCode.PlacementFailed, Message = ex.Message, TargetIndex = index };
        }
    }

    private string? ApplyDisposition(JobExecution execution)
    {
        try
        {
            Result<DispositionAuditRecord, JobError> disposed = disposition.Dispose(execution);
            if (disposed.TryGetError(out JobError? error))
                return error.Message;
            return null;
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: a disposition fault is recorded, never rolled back (the
            // copies are already safe).
            logger.LogError(ex, "Job {JobId} disposition threw", execution.Plan.JobId.Short);
            return ex.Message;
        }
    }

    private async Task<JobCompletion> RollBackAsync(JobExecution execution, JobError cause, long start, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        execution.States.Transition(JobState.RollingBack);
        Log(plan, $"rolling back: {cause.Message}");

        List<TargetRollbackItem> items = new(execution.Targets.Count);
        foreach (TargetProgress tp in execution.Targets)
            items.Add(new TargetRollbackItem
            {
                TargetIndex = tp.Plan.TargetIndex,
                State = tp.State,
                TempPath = tp.TempPath,
                FinalPath = tp.FinalPath,
                StagedPath = tp.StagedPath,
                FinalExistedBeforeJob = tp.FinalExistedBeforeJob,
            });

        RollbackContext context = new()
        {
            JobId = plan.JobId,
            Cause = cause,
            Targets = items,
            WorkspaceDir = plan.WorkspaceDir,
            OverwriteHandling = plan.Policies.OverwriteHandling,
            ExpectedContentHash = execution.Output?.ContentHash,
            Verification = plan.Policies.Verification,
        };

        // RollbackExecutor owns rollback-begin, per-target target-rolledback, workspace/staging
        // cleanup, and the terminal job-closed — the executor must NOT also close here.
        Result<RollbackResult, JobError> result = rollback.Rollback(context, ct);
        execution.States.Transition(JobState.Closed);

        bool complete = result.TryGetValue(out RollbackResult? outcome) && outcome.Complete;
        if (complete)
        {
            Log(plan, "rolled back cleanly");
            return Completed(plan, JobOutcome.Failed, null, cause, start);
        }

        IReadOnlyList<string> residuals = outcome?.ResidualPaths ?? [];
        Log(plan, $"rollback incomplete; residual paths: {string.Join(", ", residuals)}");
        return Completed(plan, JobOutcome.RollbackFailed, null, cause, start);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static JobError CanceledSeal() =>
        new() { Code = JobErrorCode.SourceUnreadable, Message = "canceled while sealing the source" };

    private static SourceConfig? FindMatchedSource(JobPlan plan)
    {
        if (!NormalizedPath.Create(plan.Payload.SourceRoot).TryGetValue(out NormalizedPath payloadRoot))
            return null;
        foreach (SourceConfig source in plan.Profile.Sources)
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root) && root == payloadRoot)
                return source;
        return null;
    }

    private JobError? TryAppend(JournalRecord record)
    {
        Result appended = journal.Append(record);
        if (appended.TryGetError(out string? error))
        {
            logger.LogError("Job {JobId}: journal append failed: {Error}", record.JobId, error);
            return new JobError { Code = JobErrorCode.JournalWriteFailed, Message = error };
        }
        return null;
    }

    private JobClosedRecord Close(JobPlan plan, JobOutcome outcome, SkipReason? skipReason = null, string? dispositionError = null) =>
        new()
        {
            JobId = plan.JobId.Value,
            Seq = 0,
            AtUtc = time.GetUtcNow(),
            Outcome = outcome,
            SkipReason = skipReason,
            DispositionError = dispositionError,
        };

    private JobCompletion Completed(JobPlan plan, JobOutcome outcome, SkipReason? skip, JobError? error, long start) =>
        new(plan.JobId, outcome, skip, error, time.GetElapsedTime(start));

    private void Log(JobPlan plan, string line) => jobLog.Append(plan.JobId.Value, line);

    private static int SeparatorCount(string value)
    {
        int count = 0;
        foreach (char c in value)
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                count++;
        return count;
    }

    private static string NormalizeSeparators(string relativePath) =>
        Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace('\\', '/');
}
