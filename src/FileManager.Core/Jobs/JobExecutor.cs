using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Files;
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
    public async Task<JobCompletion> ExecuteAsync(
        JobPlan plan, IProgress<JobProgress>? progress = null, CancellationToken ct = default)
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
            return await RunAsync(execution, progress, start, ct).ConfigureAwait(false);
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
            // Last-resort catch-and-log: the executor must never throw for a job failure. Returning
            // straight from here used to close the job Failed with whatever the placer had already
            // written still on disk, so try the sweep first — this is the only path that reaches a
            // rollback nobody anticipated needing.
            logger.LogError(ex, "Job {JobId} failed unexpectedly", plan.JobId.Short);
            JobError unexpected = new() { Code = JobErrorCode.PlacementFailed, Message = ex.Message };
            JobCompletion? swept = await TryRollBackUnexpectedAsync(execution, unexpected, progress, start).ConfigureAwait(false);
            return swept ?? Completed(plan, JobOutcome.Failed, null, unexpected, start);
        }
    }

    /// <summary>Best-effort rollback for the catch-all above. Returns null when the job is not in a
    /// state a sweep may run from (§7.1 makes RollingBack reachable only after Opened and before
    /// Committed) or when the sweep itself fails — the caller then reports the original fault, which
    /// is always the more useful one.</summary>
    private async Task<JobCompletion?> TryRollBackUnexpectedAsync(
        JobExecution execution, JobError cause, IProgress<JobProgress>? progress, long start)
    {
        if (execution.States.State
            is not (JobState.Opened or JobState.Preflighted or JobState.Screened
                or JobState.Transforming or JobState.OutputSealed or JobState.Distributing))
        {
            return null;
        }

        try
        {
            return await RollBackAsync(execution, cause, progress, start, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId}: the last-resort rollback also failed", execution.Plan.JobId.Short);
            return null;
        }
    }

    private async Task<JobCompletion> RunAsync(
        JobExecution execution, IProgress<JobProgress>? progress, long start, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        JobStateMachine states = execution.States;

        // 1. LOCK — source + every prospective final path (§4.3 step 1). Archive-destination locking
        //    is a documented Set-3 simplification (disposition is post-commit, never rolled back).
        Report(progress, plan, JobPhase.Locking);
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
        Report(progress, plan, JobPhase.Opening);
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
        Report(progress, plan, JobPhase.Preflighting);
        // lockPaths is reused, not re-derived: it already holds the source and every prospective final
        // path in normalized form, in target order (source first).
        JobError? preflightError = Preflight(plan, lockPaths);
        if (preflightError is not null)
        {
            states.Transition(JobState.Closed);
            TryAppend(Close(plan, JobOutcome.Failed));
            Log(plan, $"failed at preflight: {preflightError.Message}");
            return Completed(plan, JobOutcome.Failed, null, preflightError, start);
        }
        states.Transition(JobState.Preflighted);

        // 4. SCREEN — the authoritative filter gate (a manual single-file run is not pre-filtered).
        Report(progress, plan, JobPhase.Screening);
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

        // 5. PROBE IDENTITY (spec §3.4.1) — the unchanged-file short-circuit, run BEFORE sealing.
        //    The order is the whole point: sealing reads the source end to end, so a file that is
        //    already at every target must not reach it. Under a bounded-read LargeFileIdentity policy a
        //    duplicate is settled from a few MiB (or from metadata alone) instead of two full reads.
        Report(progress, plan, JobPhase.Sealing);
        Result<IdentityProbe, JobError> probed = await ProbeIdentityAsync(execution, ct).ConfigureAwait(false);
        if (probed.IsCanceled)
            return await RollBackAsync(execution, CanceledSeal(), progress, start, ct).ConfigureAwait(false);
        if (probed.TryGetError(out JobError? probeError))
            return await RollBackAsync(execution, probeError, progress, start, ct).ConfigureAwait(false);
        probed.TryGetValue(out IdentityProbe probe);

        if (probe.AllTargetsSatisfied)
        {
            // Every target already held identical content: close Skipped WITHOUT sealing, committing or
            // disposing (spec §3.4.1 / flow §6.2 — re-delivery is idempotent). Nothing was written and
            // nothing was sealed, so this closes directly (I-WAL).
            states.Transition(JobState.Closed);
            TryAppend(Close(plan, JobOutcome.Skipped, SkipReason.UnchangedAtAllTargets));
            Log(plan, "skipped (unchanged at all targets)");
            // Carries its resolved paths: every target already holds this content, so those paths are
            // emphatically NOT orphans and a Mirror pass must never remove them.
            return CompletedWithPaths(execution, JobOutcome.Skipped, SkipReason.UnchangedAtAllTargets, null, start);
        }

        // 6. SEAL SOURCE AS OUTPUT — no transform: the source itself is the sealed artifact. The
        //    guard-only Transforming transition keeps the §7.1 sequence; no workspace is created.
        states.Transition(JobState.Transforming);
        Result<SealedOutput, JobError> sealResult =
            await SealSourceAsync(plan, probe.SourceFullHash, ct).ConfigureAwait(false);
        if (sealResult.IsCanceled)
            return await RollBackAsync(execution, CanceledSeal(), progress, start, ct).ConfigureAwait(false);
        if (sealResult.TryGetError(out JobError? sealError))
            return await RollBackAsync(execution, sealError, progress, start, ct).ConfigureAwait(false);
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
            return await RollBackAsync(execution, sealedError, progress, start, ct).ConfigureAwait(false);
        states.Transition(JobState.OutputSealed);

        // 7. DISTRIBUTE + VERIFY + PLACE — bounded-parallel per target; cancel siblings on first fail.
        //    Targets the probe already satisfied are terminal and short-circuit inside PlaceOneAsync.
        Report(progress, plan, JobPhase.Distributing);
        states.Transition(JobState.Distributing);
        JobError? distributeError = await DistributeAsync(execution, locks, progress, ct).ConfigureAwait(false);
        if (distributeError is not null)
            return await RollBackAsync(execution, distributeError, progress, start, ct).ConfigureAwait(false);

        // The "unchanged at every target" outcome is decided in phase 5 and returns there — reaching here
        // means at least one target was placed or conflict-skipped, so the job commits.
        //
        // Commit point (I-DISPOSE): source disposition is authorized by this record and nothing else.
        // A failed commit append is fatal → rollback.
        Report(progress, plan, JobPhase.Committing, TerminalTargetCount(execution));
        JobError? commit = TryAppend(new JobCommittedRecord { JobId = plan.JobId.Value, Seq = 0, AtUtc = time.GetUtcNow() });
        if (commit is not null)
            return await RollBackAsync(execution, commit, progress, start, ct).ConfigureAwait(false);
        states.Transition(JobState.Committed);
        Log(plan, "committed");

        // 8. DISPOSE — apply OnSuccess; a disposition failure is logged in job-closed, never rolled back.
        Report(progress, plan, JobPhase.Disposing, TerminalTargetCount(execution));
        states.Transition(JobState.Disposing);
        string? dispositionError = ApplyDisposition(execution);
        states.Transition(JobState.Closed);
        TryAppend(Close(plan, JobOutcome.Succeeded, skipReason: null, dispositionError));
        Log(plan, dispositionError is null ? "succeeded" : $"succeeded (disposition error: {dispositionError})");

        // 9. RELEASE — staging holds the versions this job replaced. I-STAGING-KEEP authorizes deleting
        //    a staging dir once the job closed Succeeded, and this must happen AFTER that close is
        //    journaled: until then a crash still needs the staged originals to roll back. Recovery
        //    cannot sweep .fm_staging (it sits under arbitrary target roots it cannot enumerate), so
        //    if the success path does not clean up, every overwrite leaves a full copy of the replaced
        //    file in the user's target root forever.
        CleanUpPlacementArtifacts(execution);
        // The disposition error rides out on the completion as well as into job-closed: the journal is
        // the durable record, but the orchestrator is the only thing that can tell the user, and
        // reporting a Succeeded with a null Error is how "your sources were not disposed of" used to
        // reach nobody at all.
        return CompletedWithPaths(execution, JobOutcome.Succeeded, null, null, start)
            with { DispositionError = dispositionError };
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

    /// <param name="lockPaths">The set built by <see cref="BuildLockSet"/>: the normalized source at
    /// index 0, then one normalized prospective final path per target in target order. Reused here so
    /// the source and every target path are normalized ONCE per job rather than twice.</param>
    private JobError? Preflight(JobPlan plan, IReadOnlyList<NormalizedPath> lockPaths)
    {
        // Resolved-target checks (hard errors, spec §3.2.3) — distinct from the profile-level validator
        // checks, which cannot see the per-job resolved final paths.
        NormalizedPath source = lockPaths[0];
        Dictionary<NormalizedPath, int> seenFinalPaths = new(plan.Targets.Count);
        for (int i = 0; i < plan.Targets.Count; i++)
        {
            TargetPlan target = plan.Targets[i];
            NormalizedPath fp = lockPaths[i + 1];

            if (fp == source)
                return new JobError
                {
                    Code = JobErrorCode.SelfPathTarget,
                    Message = $"target resolves to the source path: {source.Value}",
                    Path = source.Value,
                    TargetIndex = target.TargetIndex,
                };

            // Two targets landing on one final path cannot both be placed — the path locks are
            // per-job, so they would race the same temp→final move and roll the job back. Refuse up
            // front instead. ProfileValidator rejects duplicate Targets at save time; this covers a
            // hand-edited profiles.json (and any layout that collapses two roots onto one path).
            if (seenFinalPaths.TryGetValue(fp, out int firstIndex))
                return new JobError
                {
                    Code = JobErrorCode.ConflictUnresolvable,
                    Message = $"targets {firstIndex} and {target.TargetIndex} both resolve to \"{fp.Value}\"",
                    Path = fp.Value,
                    TargetIndex = target.TargetIndex,
                };
            seenFinalPaths[fp] = target.TargetIndex;
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
        FilterInput input = FilterInput.For(plan.Source.Path, relativePath, plan.SourceMetadata, set!.HasPatternRules);
        FilterDecision decision = set.Evaluate(in input);
        decidingRule = decision.DecidingRule;
        return decision.Matched;
    }

    /// <summary>Outcome of the §3.4.1 identity probe (phase 5).</summary>
    /// <param name="AllTargetsSatisfied">Every target already holds this content, so the job is a no-op.
    /// Vacuously true for a plan with no targets, which is how that degenerate case closed before the
    /// probe existed.</param>
    /// <param name="SourceFullHash">The source's full content hash IF the probe needed one — handed to
    /// <see cref="SealSourceAsync"/> so sealing never re-reads a file the probe already read whole.</param>
    private readonly record struct IdentityProbe(bool AllTargetsSatisfied, string? SourceFullHash);

    /// <summary>Runs the spec §3.4.1 unchanged-check against every target BEFORE the output is sealed.
    ///
    /// <para>Sealing hashes the source end to end, so doing it first made a duplicate cost a full read of
    /// the source plus a full read of the destination. Probing first means a bounded-read
    /// <see cref="LargeFileIdentity"/> policy settles a duplicate from a few MiB, and the full read is
    /// paid only once it is known that something actually has to be written.</para></summary>
    private async Task<Result<IdentityProbe, JobError>> ProbeIdentityAsync(
        JobExecution execution, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        IdentityPlan idPlan = IdentityStrategy.For(
            plan.Source.SizeBytes,
            plan.Policies.Verification,
            plan.Policies.LargeFileIdentity,
            plan.Policies.LargeFileIdentityThresholdBytes);

        // Cheap gate, one stat per target: a target can only already be satisfied if it holds a file of
        // the same size. With no candidate there is nothing to compare against, so the incoming file needs
        // no digest at all — a fresh copy must not pay for one. CheckUnchangedAsync re-checks existence and
        // size authoritatively; this only decides whether to hash.
        bool anyCandidate = false;
        foreach (TargetPlan target in plan.Targets)
        {
            if (SameSizeFileExists(target.ProspectiveFinalPath, plan.Source.SizeBytes))
            {
                anyCandidate = true;
                break;
            }
        }

        string? fullHash = null;
        if (anyCandidate)
        {
            byte[]? sampledHash = null;
            switch (idPlan.ContentEvidence)
            {
                case IdentityEvidence.FullHash:
                {
                    Result<string, JobError> hashed = await hasher
                        .HashFileAsync(plan.Source.Path, plan.Policies.Verification, ct).ConfigureAwait(false);
                    if (hashed.IsCanceled)
                        return Result<IdentityProbe, JobError>.Canceled();
                    if (hashed.TryGetError(out JobError? hashError))
                        return hashError;
                    hashed.TryGetValue(out fullHash);
                    break;
                }
                case IdentityEvidence.SampledHash:
                {
                    Result<byte[], JobError> sampled = await hasher
                        .HashSampledToBytesAsync(plan.Source.Path, SampledHashLayout.Default, ct).ConfigureAwait(false);
                    if (sampled.IsCanceled)
                        return Result<IdentityProbe, JobError>.Canceled();
                    if (sampled.TryGetError(out JobError? sampledError))
                        return sampledError;
                    sampled.TryGetValue(out sampledHash);
                    break;
                }
                case null:
                    // Metadata-only policy: nothing to read.
                    break;
            }

            IdentityReference reference = new()
            {
                Path = plan.Source.Path,
                SizeBytes = plan.Source.SizeBytes,
                LastWriteUtc = plan.Source.LastWriteUtc,
                Plan = idPlan,
                FullContentHash = fullHash,
                SampledContentHash = sampledHash,
            };

            // Concurrent, like distribution: each target's existing file is a separate read, so several
            // destinations settle in max(target) rather than sum(targets). CheckUnchangedAsync never
            // throws (it owns its catch-alls), so WhenAll cannot fault.
            Task<Result<UnchangedCheckResult, JobError>>[] checks =
                new Task<Result<UnchangedCheckResult, JobError>>[plan.Targets.Count];
            for (int i = 0; i < plan.Targets.Count; i++)
                checks[i] = placer.CheckUnchangedAsync(
                    execution, i, plan.Targets[i].ProspectiveFinalPath, reference, ct);
            await Task.WhenAll(checks).ConfigureAwait(false);

            bool canceled = false;
            foreach (Task<Result<UnchangedCheckResult, JobError>> check in checks)
            {
                if (check.Result.IsCanceled)
                    canceled = true;
                else if (check.Result.TryGetError(out JobError? checkError))
                    return checkError;   // report the lowest-index failure
            }
            if (canceled)
                return Result<IdentityProbe, JobError>.Canceled();
        }

        // CheckUnchangedAsync marks the targets it satisfied, so the mirror is the authority here.
        bool allSatisfied = true;
        foreach (TargetProgress tp in execution.Targets)
        {
            if (tp.State != TargetState.SatisfiedUnchanged)
            {
                allSatisfied = false;
                break;
            }
        }

        return new IdentityProbe(allSatisfied, fullHash);
    }

    /// <summary>One stat, used only to decide whether the identity probe needs to hash anything.
    /// Unreadable metadata answers "yes, a candidate": costing a hash is the safe direction, whereas
    /// answering "no" would skip a comparison that should have happened.</summary>
    private static bool SameSizeFileExists(string path, long sizeBytes)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists && info.Length == sizeBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async Task<Result<SealedOutput, JobError>> SealSourceAsync(
        JobPlan plan, string? precomputedHash, CancellationToken ct)
    {
        string hash = "";
        if (plan.Policies.Verification is VerificationMethod.Sha256 or VerificationMethod.XxHash128)
        {
            // The identity probe may already have hashed the source in full under this very method. Reuse
            // that digest rather than reading the file a second time — under the default FullHash policy
            // this is what keeps a placement at today's cost instead of adding a read.
            if (precomputedHash is not null)
            {
                hash = precomputedHash;
            }
            else
            {
                Result<string, JobError> hashed = await hasher.HashFileAsync(plan.Source.Path, plan.Policies.Verification, ct).ConfigureAwait(false);
                if (hashed.IsCanceled)
                    return Result<SealedOutput, JobError>.Canceled();
                if (hashed.TryGetError(out JobError? hashError))
                    return hashError;
                hashed.TryGetValue(out hash!);
            }
        }
        return new SealedOutput
        {
            Path = plan.Source.Path,
            SizeBytes = plan.Source.SizeBytes,
            ContentHash = hash,
            SourceLastWriteUtc = plan.Source.LastWriteUtc,
        };
    }

    private async Task<JobError?> DistributeAsync(
        JobExecution execution, PathLockSet locks, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        JobError?[] errors = new JobError?[plan.Targets.Count];
        Task[] tasks = new Task[plan.Targets.Count];
        // Incremented as each target settles. Interlocked because the target tasks run concurrently;
        // the publisher clamps monotonically, so an out-of-order sample can never count backwards.
        int completed = 0;

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
                    return;
                }
                Report(progress, plan, JobPhase.Distributing, Interlocked.Increment(ref completed));
            }, CancellationToken.None);
        }
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A target task must not throw — PlaceOneAsync has its own catch-all — but the Task.Run
            // boundary and linked.Cancel() are outside it. Unhandled, the throw unwinds past RunAsync's
            // rollback branch into ExecuteAsync's catch-all, which closes the job Failed with every
            // temp still on disk. Converting it to a JobError keeps the failure on the rollback path.
            logger.LogError(ex, "Job {JobId}: a target task faulted during distribution", plan.JobId.Short);
            return new JobError { Code = JobErrorCode.PlacementFailed, Message = $"a target task faulted during distribution: {ex.Message}" };
        }

        foreach (JobError? error in errors)   // report the lowest-index failure
            if (error is not null)
                return error;

        // Cancellation is reported by PlaceOneAsync as "no error" on the assumption that the target
        // which failed reports the real cause. That holds for linked.Cancel() and nothing else: when
        // `ct` is cancelled from outside, every target returns null and this would read as success —
        // the job would journal job-committed and DISPOSE THE SOURCE with nothing placed and every
        // temp still on disk. An outside cancellation is a failure of the job, so it rolls back.
        if (ct.IsCancellationRequested)
            return CanceledDistribution();
        return null;
    }

    private async Task<JobError?> PlaceOneAsync(JobExecution execution, PathLockSet locks, int index, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        TargetPlan target = plan.Targets[index];
        SealedOutput output = execution.Output!;
        try
        {
            // The §3.4.1 unchanged short-circuit ran in phase 5, before sealing — see
            // ProbeIdentityAsync. A target it satisfied is terminal (the placer journalled
            // target-unchanged and marked the state), so there is nothing to write here.
            if (execution.Targets[index].State == TargetState.SatisfiedUnchanged)
            {
                Log(plan, $"target {index}: unchanged, already satisfied");
                return null;
            }

            // Conflict resolution. The rank MUST be the plan's resolved source index, not a literal:
            // the placer records placements under that same index, and a hard-coded 0 made the M:1
            // "higher-priority source keeps the file" rule (spec §3.4) unreachable.
            Result<ConflictOutcome, JobError> resolve = conflictResolver.Resolve(
                target.ProspectiveFinalPath, plan.Policies.ConflictResolution, output, plan.PriorityIndex, plan.ProfileId, locks);
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

    private async Task<JobCompletion> RollBackAsync(
        JobExecution execution, JobError cause, IProgress<JobProgress>? progress, long start, CancellationToken ct)
    {
        JobPlan plan = execution.Plan;
        Report(progress, plan, JobPhase.RollingBack, TerminalTargetCount(execution));
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
            // Resolved paths ride out even on the failure paths. A failed copy job already suppresses
            // the whole Mirror deletion phase, so this is belt-and-braces — but the guard's safe
            // direction is "do not delete", and a residual left by an incomplete rollback can still
            // hold this run's content.
            return CompletedWithPaths(execution, JobOutcome.Failed, null, cause, start);
        }

        // Residuals are empty when rollback failed before it could enumerate them (its own journal
        // append failed, so `result` carries a JobError rather than a RollbackResult).
        IReadOnlyList<string> residuals = outcome?.ResidualPaths ?? [];
        Log(plan, $"rollback incomplete; residual paths: {string.Join(", ", residuals)}");
        return CompletedWithPaths(execution, JobOutcome.RollbackFailed, null, cause, start)
            with { ResidualPaths = residuals };
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static JobError CanceledSeal() =>
        new() { Code = JobErrorCode.SourceUnreadable, Message = "canceled while sealing the source" };

    private static JobError CanceledDistribution() =>
        new() { Code = JobErrorCode.PlacementFailed, Message = "canceled while distributing to the targets" };

    /// <summary>The Source the payload came from, or null when its root matched none. Bounds-checked
    /// rather than trusting the index: the plan and the profile are separate fields, so a plan built
    /// against a different profile revision must degrade to "no per-source overrides", never throw.</summary>
    private static SourceConfig? FindMatchedSource(JobPlan plan) =>
        plan.SourceIndex >= 0 && plan.SourceIndex < plan.Profile.Sources.Count
            ? plan.Profile.Sources[plan.SourceIndex]
            : null;

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

    /// <summary>The single funnel every terminal path goes through — which is why
    /// <see cref="JobCompletion.SourceBytes"/> is stamped here rather than at each of them.</summary>
    private JobCompletion Completed(JobPlan plan, JobOutcome outcome, SkipReason? skip, JobError? error, long start) =>
        new(plan.JobId, outcome, skip, error, time.GetElapsedTime(start))
        {
            // The plan's own recorded size, not a fresh stat: the run's denominator was summed from the
            // sizes the PLAN saw, so re-measuring a file that grew since would make the numerator
            // outrun a total it is not measured against.
            SourceBytes = plan.Source.SizeBytes,
        };

    /// <summary>As <see cref="Completed"/>, plus the destination paths this execution actually resolved.
    /// Used on the terminal paths that reached target processing, so a run-scoped consumer (the Mirror
    /// deletion pass) can be certain it never removes a path this job wrote to — including one that
    /// conflict resolution chose at execution time and the plan therefore never predicted.</summary>
    private JobCompletion CompletedWithPaths(
        JobExecution execution, JobOutcome outcome, SkipReason? skip, JobError? error, long start) =>
        Completed(execution.Plan, outcome, skip, error, start) with { ResolvedFinalPaths = ResolvedPaths(execution) };

    /// <summary>Each target's post-conflict-resolution final path, falling back to the prospective one
    /// for a target that never got as far as resolving (so the set is a superset of what was written —
    /// the safe direction for a guard that decides what NOT to delete).</summary>
    private static IReadOnlyList<string> ResolvedPaths(JobExecution execution)
    {
        List<string> paths = new(execution.Targets.Count);
        foreach (TargetProgress tp in execution.Targets)
            paths.Add(tp.FinalPath ?? tp.Plan.ProspectiveFinalPath);
        return paths;
    }

    private void Log(JobPlan plan, string line) => jobLog.Append(plan.JobId.Value, line);

    /// <summary>Pushes one progress sample. Progress is decoration: a throwing sink must never change
    /// a job's outcome, so it is caught and logged here rather than propagating into the phase
    /// algorithm. Throttling and monotonic clamping are the publisher's job, not ours.</summary>
    private void Report(IProgress<JobProgress>? progress, JobPlan plan, JobPhase phase, int completed = 0)
    {
        if (progress is null)
            return;
        try
        {
            progress.Report(new JobProgress(phase, completed, plan.Targets.Count));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId}: progress sink threw while reporting {Phase}", plan.JobId.Short, phase);
        }
    }

    /// <summary>Releases this job's transient placement artifacts after a successful, journaled close:
    /// the staging dirs holding the versions it replaced, and the workspace. Entirely best-effort —
    /// the job is already committed and its targets verified, so a cleanup failure must never change
    /// the outcome; it only leaves disk to reclaim, which is logged.</summary>
    private void CleanUpPlacementArtifacts(JobExecution execution)
    {
        HashSet<string> stagingDirs = new(StringComparer.OrdinalIgnoreCase);
        foreach (TargetProgress tp in execution.Targets)
        {
            if (tp.StagedPath is null)
                continue;
            string? dir = Path.GetDirectoryName(tp.StagedPath);
            if (!string.IsNullOrEmpty(dir))
                stagingDirs.Add(dir);
        }

        foreach (string dir in stagingDirs)
        {
            TryDeleteDirectory(execution.Plan, dir);
            InfrastructurePaths.TryDropSharedStagingParent(dir);
        }

        TryDeleteDirectory(execution.Plan, execution.Plan.WorkspaceDir);
    }

    private void TryDeleteDirectory(JobPlan plan, string directory)
    {
        // Last-resort log-and-continue: this runs after the job is committed and closed, so nothing
        // here may surface as a job failure.
        if (InfrastructurePaths.TryDeleteDirectory(directory) is Exception ex)
        {
            logger.LogWarning(ex, "Job {JobId}: could not clean up \"{Directory}\"", plan.JobId.Short, directory);
            Log(plan, $"cleanup warning: could not delete {directory}: {ex.Message}");
        }
    }

    /// <summary>How many targets have reached a terminal state — used for the progress samples of the
    /// phases after distribution, where the per-target counter is out of scope.</summary>
    private static int TerminalTargetCount(JobExecution execution)
    {
        int done = 0;
        foreach (TargetProgress tp in execution.Targets)
            if (tp.State is TargetState.Placed or TargetState.SatisfiedUnchanged or TargetState.SkippedConflict)
                done++;
        return done;
    }

}
