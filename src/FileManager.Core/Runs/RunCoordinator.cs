using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Runs;

/// <summary>Drives a manual run through plan → approve → execute → close.
///
/// <para><b>Why the plan comes first.</b> Capturing the work before doing any of it buys four things at
/// once that were previously impossible: the user can approve an itemized list instead of an intention;
/// the work cannot drift with the filesystem underneath the run; progress has a real denominator; and
/// — the reason Mirror needs it — there is a single agreed list of orphans, produced by the same
/// planner that produced the preview, so the deletions performed are provably the deletions
/// shown.</para>
///
/// <para>The copy half still flows through <see cref="ITriggerQueue"/> →
/// <c>JobOrchestrator</c> → <c>IJobExecutor</c>, entirely unchanged. The executor re-screens and
/// re-checks-unchanged, so it can only ever do LESS than the plan predicted, never more — which is
/// what makes the plan safe to treat as a ceiling.</para></summary>
public sealed class RunCoordinator(
    IProfilePlanner planner,
    ITriggerQueue queue,
    IMirrorDeletionPass deletionPass,
    IEngineEventBus eventBus,
    IProfileValidator validator,
    IProfileCatalog catalog,
    EnginePaths paths,
    EngineConfig config,
    TimeProvider time,
    ILogger<RunCoordinator> logger) : IRunCoordinator
{
    /// <summary>How often the drain loop re-checks the barrier. A run takes seconds to minutes, so this
    /// costs nothing and keeps the barrier logic a readable loop rather than a web of callbacks.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a closed run stays queryable, so a client that asks just after the terminal
    /// event still gets an answer instead of a null.</summary>
    private static readonly TimeSpan ClosedRunRetention = TimeSpan.FromMinutes(10);

    /// <summary>Minimum spacing between planning-progress samples. Matches the ~10 frames/sec the dry-run
    /// stream used, which was measured to be enough for a caption to look live without the publish itself
    /// becoming the scan's bottleneck.</summary>
    private static readonly TimeSpan PlanProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    private readonly CancellationTokenSource _shutdown = new();

    public Result<RunHandle, string> Begin(Profile profile, string? scopePath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_shutdown.IsCancellationRequested)
            return "the service is shutting down";

        RunState run = new(Guid.NewGuid(), profile, scopePath);
        _runs[run.RunId] = run;
        PruneClosedRuns();

        // Planning runs detached so the IPC caller is not held behind a scan (§8 rule 5). Under the
        // coordinator's shutdown token, not None, so teardown can stop a walk in progress.
        run.Work = Task.Run(() => PlanAsync(run), CancellationToken.None);
        logger.LogInformation(
            "Run {RunId} started planning profile {ProfileId} ({Scope})",
            run.RunId, profile.Id, scopePath ?? "whole profile");
        return new RunHandle { RunId = run.RunId, ProfileId = profile.Id };
    }

    public Result Approve(Guid runId, bool approve, bool acknowledgeWarnings = false)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return $"no run with id {runId}";
        if (!approve)
        {
            // Declining is not a failure and must leave the filesystem exactly as it was — nothing has
            // been touched at this point, by construction. Never gated: abandoning a plan is always
            // allowed, whatever the validator thinks of the profile.
            if (!CloseUnstarted(run, RunPhase.AwaitingApproval, out string? declineError))
                return declineError!;
            logger.LogInformation("Run {RunId} was declined; nothing was changed", runId);
            return Result.Success();
        }

        // THE gate. Read outside run.Gate: Validate walks the profile and compiles its filters, which is
        // not work to do while holding a lock the orchestrator's worker threads take on every settle.
        // The phase is re-checked under the lock below, so a race here can only mean a redundant
        // validation, never a run that executes ungated.
        if (Refusal(run, acknowledgeWarnings) is { } refusal)
        {
            logger.LogWarning("Run {RunId} was not approved: {Refusal}", runId, refusal);
            return refusal;
        }

        lock (run.Gate)
        {
            if (run.Phase != RunPhase.AwaitingApproval)
                return $"run {runId} is {run.Phase}, not awaiting approval";
            run.Phase = RunPhase.Executing;
        }
        run.Work = Task.Run(() => ExecuteAsync(run), CancellationToken.None);
        return Result.Success();
    }

    /// <summary>Why this run must not start, or null.
    ///
    /// <para>An <c>Error</c> is fatal: the profile cannot be run at all. A <c>BlockingWarning</c> is
    /// acknowledgeable, exactly as on the save path — and is only asked about for a configuration that
    /// never went through it. A profile persisted in the catalog has already been acknowledged, because
    /// <c>SaveProfileAsync</c> refuses to store one whose warnings were not; re-asking on every run of a
    /// saved Mirror profile would be warning fatigue on the one prompt that guards target-side deletion,
    /// which is the opposite of the intent.</para></summary>
    private string? Refusal(RunState run, bool acknowledgeWarnings)
    {
        IReadOnlyList<ValidationIssue> issues = ValidationIssuesFor(run);
        foreach (ValidationIssue issue in issues)
        {
            if (issue.Severity == ValidationSeverity.Error)
                return $"the profile is not valid: {issue.Code} — {issue.Message}";
        }
        if (acknowledgeWarnings || AlreadyAcknowledgedOnSave(run.Profile))
            return null;
        foreach (ValidationIssue issue in issues)
        {
            if (issue.Severity == ValidationSeverity.BlockingWarning)
                return $"this run has not been acknowledged: {issue.Code} — {issue.Message}";
        }
        return null;
    }

    /// <summary>Whether this exact configuration is the one sitting in the catalog — which means it got
    /// through <c>SaveProfileAsync</c>, which means its blocking warnings were acknowledged there.
    ///
    /// <para>Compared by serialized form, not by <c>==</c>: <see cref="Profile"/> is a record whose
    /// members include lists, so reference equality on those makes two identical profiles unequal and
    /// every run would look like an unsaved draft. The canonical JSON is the same form
    /// <c>profiles.json</c> stores, so "equal here" means "equal as saved".</para>
    ///
    /// <para>A profile hand-edited in <c>profiles.json</c> is treated as acknowledged, since it is
    /// indistinguishable from a saved one. That is the pre-existing reach of the save-path gate, not
    /// something this widens.</para></summary>
    private bool AlreadyAcknowledgedOnSave(Profile planned)
    {
        foreach (Profile persisted in catalog.All)
        {
            if (persisted.Id != planned.Id)
                continue;
            return string.Equals(Canonical(persisted), Canonical(planned), StringComparison.Ordinal);
        }
        // In no catalog at all: a draft that was never saved, so nothing ever acknowledged it.
        return false;

        static string Canonical(Profile profile) =>
            JsonSerializer.Serialize(profile, FileManagerJsonContext.Default.Profile);
    }

    /// <summary>Validates the profile the run was PLANNED against — never the catalog's current copy,
    /// which may have been edited or deleted since, and for an unsaved draft was never there at all.
    /// <para>Cached on the run: it is computed once when planning finishes (so <c>run-planned</c> can
    /// carry it) and read again on approval, and re-validating would let the answer the user acknowledged
    /// differ from the answer that gates them.</para></summary>
    private IReadOnlyList<ValidationIssue> ValidationIssuesFor(RunState run)
    {
        lock (run.Gate)
        {
            if (run.ValidationIssues is { } cached)
                return cached;
        }
        List<Profile> others = [];
        foreach (Profile other in catalog.All)
            if (other.Active && other.Id != run.Profile.Id)
                others.Add(other);
        IReadOnlyList<ValidationIssue> issues = validator.Validate(run.Profile, others);
        lock (run.Gate)
            run.ValidationIssues ??= issues;
        return issues;
    }

    /// <summary>Closes a run that never started doing anything, dropping its snapshot first.
    /// <para>Shared by decline and by a cancel that arrives after planning finished, so the two cannot
    /// drift: both are "this plan will never execute", and both must leave a run that
    /// <see cref="PruneClosedRuns"/> can eventually reap. Returns false with a message when the run is
    /// not in <paramref name="expected"/>.</para></summary>
    private bool CloseUnstarted(RunState run, RunPhase expected, out string? error)
    {
        lock (run.Gate)
        {
            if (run.Phase != expected)
            {
                error = $"run {run.RunId} is {run.Phase}, not {expected}";
                return false;
            }
        }
        CleanUpSnapshot(run);   // before Closed is observable — see Close()
        lock (run.Gate)
        {
            run.Phase = RunPhase.Closed;
            run.Outcome = RunOutcome.Cancelled;
            run.ClosedAt = time.GetUtcNow();
        }
        PublishCompleted(run);
        error = null;
        return true;
    }

    public Result Cancel(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return $"no run with id {runId}";
        run.Cancel.Cancel();
        // Work not yet started need not happen. Work already in flight is never interrupted
        // (I-ATOMIC-JOB) — the drain loop waits for it, and the deletion phase is skipped.
        int dropped = queue.DropRun(runId);
        lock (run.Gate)
            run.Cancelled = true;
        logger.LogInformation("Run {RunId} cancelled; dropped {Dropped} pending payload(s)", runId, dropped);
        return Result.Success();
    }

    public RunStatus? GetStatus(Guid runId) =>
        _runs.TryGetValue(runId, out RunState? run) ? run.Snapshot() : null;

    public string? SnapshotDirectory(Guid runId) =>
        _runs.TryGetValue(runId, out RunState? run) ? run.Directory : null;

    public Profile? PlannedProfile(Guid runId) =>
        _runs.TryGetValue(runId, out RunState? run) ? run.Profile : null;

    public void Settled(Guid runId, JobCompletion? completion)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return;
        lock (run.Gate)
        {
            run.Settled++;
            run.LastSettleTicks = time.GetTimestamp();
            switch (completion?.Outcome)
            {
                case JobOutcome.Succeeded: run.Succeeded++; break;
                case JobOutcome.Skipped: run.SkippedJobs++; break;
                case JobOutcome.Failed:
                case JobOutcome.RollbackFailed: run.Failed++; break;
                // A null completion is a payload the orchestrator DROPPED before the executor ran (its
                // profile vanished or went inactive, or its plan could not be built). It settles the
                // barrier — that is the whole point of reporting it — but it is not an outcome, so it
                // is counted apart from Failed rather than folded into it. It is still work from the
                // approved list that did not happen, which is why the deletion gate reads it too.
                default: run.Dropped++; break;
            }
            if (completion is not null)
                foreach (string written in completion.ResolvedFinalPaths)
                    run.PathsWritten.Add(written);
        }
    }

    public void Coalesced(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return;
        // A payload of this run was superseded in the queue and will never produce a job, so the
        // barrier must stop expecting one. Without this the run waits out its whole deadline and its
        // Mirror deletion phase is then refused for a reason that has nothing to do with safety.
        lock (run.Gate)
            run.Expected--;
    }

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        List<Task> pending = [];
        foreach (RunState run in _runs.Values)
        {
            run.Cancel.Cancel();
            if (run.Work is { } work)
                pending.Add(work);
        }
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: every run body has its own catch-all, so this can only be a
            // cancellation unwind, and teardown must finish regardless.
            logger.LogDebug(ex, "A run did not unwind cleanly during shutdown");
        }
        _shutdown.Dispose();
    }

    // ---- planning ----------------------------------------------------------------------------------

    private async Task PlanAsync(RunState run)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, run.Cancel.Token);
        string directory = RunSnapshotPaths.DirectoryFor(paths, run.RunId);
        run.Directory = directory;
        PlanState state = new();
        // Counts entries the source walk could not read. SourceScanner downgrades an unopenable
        // subdirectory to a Warning so its siblings are still walked, which means the plan LOOKS
        // complete — and real source files sit behind that fault, absent from the survivor set. For a
        // Mirror run that is the difference between a correct orphan list and one that names files
        // whose source it simply never saw, so it has to reach the deletion gate.
        DryRunProgressCounters counters = new();
        string? failure = null;

        try
        {
            using RunSnapshotWriter writer = new(directory, logger);
            // Seeded with a real timestamp, never left at 0: GetElapsedTime(0) reads as machine uptime,
            // which would make the interval below meaningless.
            long lastProgress = time.GetTimestamp();
            await foreach (Result<PlanChunk, string> chunk in
                planner.PlanAsync(run.Profile, run.ScopePath, state, counters, linked.Token)
                    .ConfigureAwait(false))
            {
                if (chunk.TryGetError(out string? error))
                {
                    failure = error;
                    break;
                }
                chunk.TryGetValue(out PlanChunk planned);
                writer.Consume(planned, run.Profile);
                // Live scan counts, so a multi-minute preview is distinguishable from a wedged one. Chunks
                // already arrive in batches, and the interval bounds it again for a tree of small chunks;
                // the event is lossy by contract, so a dropped sample costs nothing.
                if (time.GetElapsedTime(lastProgress) >= PlanProgressInterval)
                {
                    lastProgress = time.GetTimestamp();
                    PublishPlanningProgress(run, counters);
                }
            }

            if (failure is null && writer.Failure is { } writeFailure)
                failure = writeFailure;

            if (failure is null)
            {
                run.PlannedCopies = writer.CopyCount;
                run.PlannedDeletes = writer.DeleteCount;
                run.PlannedCopyBytes = writer.CopyBytes;
                run.PlannedDeleteBytes = writer.DeleteBytes;
                run.PlanTruncated = state.Truncated;
                run.SweepFaultDetail = state.SweepFaultDetail;
                run.UnreadableSourceEntries = (int)Math.Min(int.MaxValue, counters.Skipped);
                Result completed = writer.Complete(new RunSnapshotHeader
                {
                    RunId = run.RunId,
                    Profile = run.Profile,
                    ScopePath = run.ScopePath,
                    PlannedAtUtc = time.GetUtcNow(),
                    CopyItemCount = writer.CopyCount,
                    DeleteItemCount = writer.DeleteCount,
                    CopyBytes = writer.CopyBytes,
                    DeleteBytes = writer.DeleteBytes,
                    SourceItemCount = writer.SourceCount,
                    DestinationItemCount = writer.DestinationCount,
                    SweptFilesByTargetRoot = writer.SweptByTargetRoot,
                    Truncated = state.Truncated,
                    SweepFaultDetail = state.SweepFaultDetail,
                    Space = state.Space,
                });
                completed.TryGetError(out failure);
            }
        }
        catch (OperationCanceledException)
        {
            failure = run.Cancel.IsCancellationRequested
                ? "the run was cancelled while planning"
                : "the service shut down while planning";
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log at this task boundary: planning is detached, and an unexpected
            // throw must become a failed run rather than an unobserved exception.
            logger.LogError(ex, "Run {RunId} failed while planning", run.RunId);
            failure = ex.Message;
        }

        if (failure is not null || run.Cancelled)
        {
            CleanUpSnapshot(run);   // before Closed is observable — see Close()
            lock (run.Gate)
            {
                run.Phase = RunPhase.Closed;
                run.Outcome = run.Cancelled ? RunOutcome.Cancelled : RunOutcome.PlanFailed;
                run.PlanError = failure;
                run.ClosedAt = time.GetUtcNow();
            }
            PublishPlanned(run, failure);
            PublishCompleted(run);
            return;
        }

        // Computed BEFORE the phase becomes AwaitingApproval, so an approval arriving the instant the
        // phase flips cannot beat the list it is gated on into existence.
        ValidationIssuesFor(run);

        lock (run.Gate)
            run.Phase = RunPhase.AwaitingApproval;
        logger.LogInformation(
            "Run {RunId} planned: {Copies} copy item(s), {Deletes} deletion(s){Truncated}",
            run.RunId, run.PlannedCopies, run.PlannedDeletes, run.PlanTruncated ? " (TRUNCATED)" : "");
        PublishPlanned(run, error: null);

        // An unreadable subdirectory is downgraded to a warning by the scanner so its siblings are
        // still walked, which means the plan looks complete. Saying so at APPROVAL time is the point:
        // otherwise the user approves a run — possibly one whose OnSuccess is PermanentDelete — over a
        // tree that was only partly covered.
        if (run.UnreadableSourceEntries > 0)
            Warn($"{run.UnreadableSourceEntries} item(s) under the sources of \"{run.Profile.Name}\" could not be "
                + "read and are missing from this plan (see the service log for details)."
                + (run.Profile.SyncMode == SyncMode.Mirror
                    ? " No destination files will be deleted, because files whose source could not be read would look like orphans."
                    : ""));

        // A partial plan is worth saying out loud at approval time, not only afterwards: it is the
        // difference between "this is what will happen" and "this is some of what will happen".
        if (run.PlanTruncated)
            Warn($"The plan for \"{run.Profile.Name}\" does not cover everything it scanned"
                + (run.SweepFaultDetail is { } detail ? $" ({detail})" : "")
                + (run.Profile.SyncMode == SyncMode.Mirror
                    ? ". No destination files will be deleted, because an incomplete plan's orphan list cannot be trusted."
                    : "."));
    }

    // ---- execution ---------------------------------------------------------------------------------

    private async Task ExecuteAsync(RunState run)
    {
        try
        {
            Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(run.Directory!);
            if (read.TryGetError(out string? headerError))
            {
                Close(run, RunOutcome.PlanFailed, headerError);
                return;
            }
            read.TryGetValue(out RunSnapshotHeader? header);

            bool mirror = header!.Profile.SyncMode == SyncMode.Mirror;
            bool proactive = header.Profile.Policies.MirrorDeletion == MirrorDeletion.Proactive;

            // Proactive deletes BEFORE the copies land, to free the space they need. Because the work
            // list is already frozen, this costs nothing extra — there is no second enumeration to do,
            // which is the only reason the ordering is cheap enough to offer as a setting.
            if (mirror && proactive && !run.Cancelled)
                await DeleteOrphansAsync(run, header).ConfigureAwait(false);

            if (!run.Cancelled)
            {
                EnqueueCopies(run, header);
            }
            else
            {
                // Nothing was queued, so nothing will settle. Saying so is what lets the barrier below
                // return at once instead of polling out its whole deadline: every one of its exit
                // conditions is gated on EnqueueComplete, so a run cancelled before this point would
                // otherwise sit in the drain loop for MirrorBarrierTimeout.
                lock (run.Gate)
                {
                    run.EnqueueComplete = true;
                    run.LastSettleTicks = time.GetTimestamp();
                }
            }

            await AwaitDrainAsync(run).ConfigureAwait(false);

            if (mirror && !proactive && !run.Cancelled)
                await DeleteOrphansAsync(run, header).ConfigureAwait(false);

            RunOutcome outcome = run.Cancelled
                ? RunOutcome.Cancelled
                : run.Failed > 0 || run.Dropped > 0
                  || run.DeletionAbortReason is not null || run.DeletionFailures.Count > 0
                    ? RunOutcome.CompletedWithProblems
                    : RunOutcome.Succeeded;
            Close(run, outcome, planError: null);
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: ExecuteAsync is detached and must never let a throw escape.
            logger.LogError(ex, "Run {RunId} failed while executing", run.RunId);
            Close(run, RunOutcome.CompletedWithProblems, ex.Message);
        }
    }

    /// <summary>Turns the snapshot's copy items into payloads. Each carries the run id so its job
    /// settles this run's barrier and so a cancel can drop what has not started.</summary>
    private void EnqueueCopies(RunState run, RunSnapshotHeader header)
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (RunCopyItem item in RunSnapshotReader.ReadCopies(run.Directory!, logger))
        {
            if (run.Cancelled || _shutdown.IsCancellationRequested)
                break;
            // Metadata deliberately null: JobPlanFactory re-stats, so the executor sees current truth
            // and its own screen/unchanged gates stay authoritative.
            EnqueueOutcome outcome = queue.Enqueue(new Payload(
                header.Profile.Id, item.SourcePath, item.SourceRoot, TriggerKind.ManualShell, now,
                Metadata: null, RunId: run.RunId));

            // One pending entry produces exactly one job, and it settles whichever run the payload the
            // entry now holds belongs to — us. So we expect a job when we took a new place in the
            // queue, and when we took OVER an entry another run was holding; but not when we coalesced
            // onto our own pending payload, which was already counted. Incrementing unconditionally
            // left Expected permanently above the number of jobs that can ever settle, so the exact
            // barrier could not be met and the run waited out its whole deadline — which then reads as
            // unaccounted copies and refuses the deletion phase for an accounting artifact.
            bool producesOurJob = outcome.Queued
                || (outcome.Displaced is { RunId: { } other } && other != run.RunId);
            if (producesOurJob)
            {
                lock (run.Gate)
                    run.Expected++;
            }

            // The payload we displaced will never produce a job. If it belonged to another run, that
            // run has to stop expecting one, or its barrier waits out the whole deadline.
            if (outcome.Displaced is { RunId: { } displacedRun } && displacedRun != run.RunId)
                Coalesced(displacedRun);
        }
        lock (run.Gate)
        {
            run.EnqueueComplete = true;
            // The quiescence window means "nothing of ours has settled for a while". Until the first
            // job settles there is nothing to measure from, and the moment the work list was fully
            // queued is the honest starting point. Left at its 0 default it read as machine uptime, so
            // the window was satisfied on the very first poll and the run was declared drained while
            // its copies were still in flight — which zeroes the two counters the deletion pass's
            // self-write and incomplete-copy guards depend on.
            run.LastSettleTicks = time.GetTimestamp();
        }
    }

    /// <summary>Waits for every job this run enqueued to reach a terminal state.
    ///
    /// <para>The barrier is exact — the snapshot gave the count up front — with two backstops, and both
    /// fail in the safe direction. <b>Quiescence</b> covers an accounting hole (a payload dropped
    /// somewhere that never reported settling): when nothing of ours is queued and nothing of ours has
    /// settled for a while, there is nothing left to wait for. <b>The deadline</b> covers everything
    /// else, and expiring it does NOT release the deletion phase — a run whose copies cannot be
    /// accounted for must not go on to delete anything.</para></summary>
    private async Task AwaitDrainAsync(RunState run)
    {
        long started = time.GetTimestamp();
        while (true)
        {
            lock (run.Gate)
            {
                if (run.EnqueueComplete && run.Settled >= run.Expected)
                    return;
            }
            if (_shutdown.IsCancellationRequested)
                return;

            try
            {
                await Task.Delay(DrainPollInterval, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            PublishProgress(run);

            bool quiescent;
            lock (run.Gate)
            {
                quiescent = run.EnqueueComplete
                    && queue.PendingCountForRun(run.RunId) == 0
                    && time.GetElapsedTime(run.LastSettleTicks) > config.MirrorQuiescenceWindow
                    && run.Settled < run.Expected;
            }
            if (quiescent)
            {
                logger.LogWarning(
                    "Run {RunId}: {Settled} of {Expected} payload(s) reported settling, but nothing is queued " +
                    "and nothing has settled for {Window} — treating the run as drained",
                    run.RunId, run.Settled, run.Expected, config.MirrorQuiescenceWindow);
                return;
            }

            if (time.GetElapsedTime(started) >= config.MirrorBarrierTimeout)
            {
                // Deliberately NOT "assume it drained": the deletion phase reads Failed/unaccounted
                // work as a reason to delete nothing, and that is the correct outcome here.
                lock (run.Gate)
                    run.BarrierTimedOut = true;
                logger.LogWarning(
                    "Run {RunId}: waited {Timeout} for {Expected} payload(s) and only {Settled} settled; " +
                    "giving up on the barrier",
                    run.RunId, config.MirrorBarrierTimeout, run.Expected, run.Settled);
                return;
            }
        }
    }

    private async Task DeleteOrphansAsync(RunState run, RunSnapshotHeader header)
    {
        List<RunDeleteItem> orphans = [.. RunSnapshotReader.ReadDeletes(run.Directory!, logger)];
        if (orphans.Count == 0)
            return;

        HashSet<string> written;
        int failed;
        bool timedOut;
        int unreadable;
        lock (run.Gate)
        {
            written = new HashSet<string>(run.PathsWritten, StringComparer.OrdinalIgnoreCase);
            // Dropped counts with Failed here and nowhere else. To this gate the two are the same fact
            // — a file from the approved list whose copy did not land — and the pass must refuse either
            // way, or it reconciles the destination against a source set it never finished copying.
            failed = run.Failed + run.Dropped;
            timedOut = run.BarrierTimedOut;
            unreadable = run.UnreadableSourceEntries;
        }

        MirrorDeletionResult result = await deletionPass.DeleteAsync(new MirrorDeletionRequest
        {
            PassId = JobId.New(),
            RunId = run.RunId,
            Profile = header.Profile,
            Orphans = orphans,
            ScopePath = header.ScopePath,
            PlanTruncated = header.Truncated,
            // Three distinct ways of not knowing enough, all reaching the same gate: part of the
            // destination tree was unwalkable, part of the SOURCE tree was unreadable (which leaves the
            // plan looking complete while its survivor set is not), or the copy barrier could not be
            // accounted for so we cannot say every copy landed.
            EnumerationIncomplete = header.SweepFaultDetail is not null || unreadable > 0 || timedOut,
            CopyJobsFailed = failed,
            PathsWrittenByThisRun = written,
            SweptFilesByTargetRoot = SweptByRoot(header, orphans),
        }, _shutdown.Token).ConfigureAwait(false);

        lock (run.Gate)
        {
            run.Deleted = result.Deleted;
            run.BytesDeleted = result.BytesDeleted;
            run.DeletionAbortReason = result.AbortReason;
            foreach (MirrorDeletionFailure failure in result.Failures)
                run.DeletionFailures.Add($"{failure.Path}: {failure.Reason}");
        }

        if (result.AbortReason is { } reason)
            Warn($"\"{header.Profile.Name}\": no destination files were deleted — {reason}");
        else if (result.Failures.Count > 0)
            Warn($"\"{header.Profile.Name}\": {result.Failures.Count} destination file(s) could not be deleted "
                + $"(first: {result.Failures[0].Path} — {result.Failures[0].Reason}). See the run log for the rest.");
    }

    /// <summary>Files the plan's sweep saw under each target root, for the pass's ratio guard.
    ///
    /// <para>Read from the snapshot header, where the sweep counted them as it classified them — the one
    /// place each destination file is seen exactly once. This used to be RECONSTRUCTED here as "orphans
    /// under this root plus the run's copy items", on the premise that the non-orphans are the survivors
    /// the copy items account for. That premise is false: an already-identical file is
    /// <c>SkippedUnchanged</c> and produces no copy item, so a synchronized profile's denominator
    /// collapsed to its own orphan count and the guard refused every steady-state pass at 100%.</para>
    ///
    /// <para>A snapshot predating the header field falls back to the orphan tally alone, which refuses
    /// the pass: an under-estimate can only make the guard stricter, which is the safe direction for a
    /// guard whose job is to refuse.</para></summary>
    private static IReadOnlyDictionary<string, int> SweptByRoot(
        RunSnapshotHeader header, IReadOnlyList<RunDeleteItem> orphans)
    {
        if (header.SweptFilesByTargetRoot.Count > 0)
            return new Dictionary<string, int>(header.SweptFilesByTargetRoot, StringComparer.OrdinalIgnoreCase);

        // A snapshot from before the header carried the counts. The orphans alone are all that can be
        // recovered, which makes every root look 100% orphaned and refuses the pass — the strict
        // direction, and the right one for a plan whose survivor set cannot be established.
        Dictionary<string, int> swept = new(StringComparer.OrdinalIgnoreCase);
        foreach (RunDeleteItem orphan in orphans)
            swept[orphan.TargetRoot] = swept.GetValueOrDefault(orphan.TargetRoot) + 1;
        return swept;
    }

    // ---- lifecycle plumbing ------------------------------------------------------------------------

    private void Close(RunState run, RunOutcome outcome, string? planError)
    {
        // Drop the scaffolding BEFORE the terminal phase becomes observable. Closed is what every
        // client reads as "this run is over", and GetStatus/SnapshotDirectory are lock-free, so
        // setting the phase first left a window in which a closed run still had its snapshot
        // directory on disk and still handed out a path to it.
        CleanUpSnapshot(run);
        lock (run.Gate)
        {
            run.Phase = RunPhase.Closed;
            run.Outcome = outcome;
            run.PlanError ??= planError;
            run.ClosedAt = time.GetUtcNow();
        }
        PublishCompleted(run);
        logger.LogInformation(
            "Run {RunId} closed {Outcome}: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, " +
            "{Dropped} dropped, {Deleted} deleted",
            run.RunId, outcome, run.Succeeded, run.SkippedJobs, run.Failed, run.Dropped, run.Deleted);
    }

    /// <summary>Drops the run's frozen work list once the run is over. Best-effort: a failure here must
    /// never affect an outcome, since the run's real record is the journal and the audit trail.
    /// <para>[flagged] A swallowed failure leaks the directory permanently — there is no startup sweep
    /// of <see cref="EnginePaths.RunsDirectory"/> despite what its doc comment used to claim, and
    /// nothing else enumerates it. The warning below is the only trace.</para></summary>
    private void CleanUpSnapshot(RunState run)
    {
        if (run.Directory is null)
            return;
        if (InfrastructurePaths.TryDeleteDirectory(run.Directory) is Exception ex)
        {
            // Kept on the run so the path stays reportable; the startup sweep collects it later.
            logger.LogWarning(ex, "Could not delete the run snapshot at {Directory}", run.Directory);
            return;
        }
        // Forget the path once it is really gone, so SnapshotDirectory stops handing out a directory
        // that no longer exists — the plan-replay handler reads a null as RUN_NOT_FOUND, which is the
        // truth for a closed run, where a stale path would surface as an opaque I/O error instead.
        run.Directory = null;
    }

    /// <summary>Forgets long-closed runs so a long-lived service does not accumulate one per
    /// invocation, while keeping recent ones queryable.</summary>
    private void PruneClosedRuns()
    {
        DateTimeOffset cutoff = time.GetUtcNow() - ClosedRunRetention;
        foreach ((Guid id, RunState run) in _runs)
            if (run.ClosedAt is { } closed && closed < cutoff)
                _runs.TryRemove(id, out _);
    }

    private void PublishPlanned(RunState run, string? error) => Publish(new RunPlannedEvent
    {
        AtUtc = time.GetUtcNow(),
        RunId = run.RunId,
        ProfileId = run.Profile.Id,
        PlannedCopies = run.PlannedCopies,
        PlannedDeletes = run.PlannedDeletes,
        PlannedCopyBytes = run.PlannedCopyBytes,
        PlannedDeleteBytes = run.PlannedDeleteBytes,
        Truncated = run.PlanTruncated,
        Error = error,
        // What the footer has to state and the user has to acknowledge before Approve will be honoured.
        // Only the blocking half: a plain Warning does not gate anything, so listing it beside the
        // Approve button would spend the user's attention on something they cannot act on.
        BlockingIssues = BlockingIssuesOf(run),
    });

    private static IReadOnlyList<ValidationIssue> BlockingIssuesOf(RunState run)
    {
        lock (run.Gate)
        {
            if (run.ValidationIssues is not { } issues)
                return [];
            List<ValidationIssue> blocking = [];
            foreach (ValidationIssue issue in issues)
                if (issue.Severity is ValidationSeverity.Error or ValidationSeverity.BlockingWarning)
                    blocking.Add(issue);
            return blocking;
        }
    }

    /// <summary>A progress sample from the PLANNING phase, where the copy counters are all still zero and
    /// the scan counts are the only live figures — there is no denominator yet, because computing one is
    /// what planning is doing.</summary>
    private void PublishPlanningProgress(RunState run, DryRunProgressCounters counters) =>
        Publish(new RunProgressEvent
        {
            AtUtc = time.GetUtcNow(),
            RunId = run.RunId,
            Phase = RunPhase.Planning.ToString(),
            Completed = 0,
            Total = 0,
            Deleted = 0,
            ScannedSources = counters.Sources,
            ScannedDestinations = counters.Destinations,
        });

    private void PublishProgress(RunState run)
    {
        RunStatus status = run.Snapshot();
        Publish(new RunProgressEvent
        {
            AtUtc = time.GetUtcNow(),
            RunId = run.RunId,
            Phase = status.Phase.ToString(),
            Completed = status.Succeeded + status.Skipped + status.Failed,
            Total = status.PlannedCopies,
            Deleted = status.Deleted,
        });
    }

    private void PublishCompleted(RunState run)
    {
        RunStatus status = run.Snapshot();
        Publish(new RunCompletedEvent
        {
            AtUtc = time.GetUtcNow(),
            RunId = run.RunId,
            ProfileId = run.Profile.Id,
            Outcome = status.Outcome.ToString(),
            Succeeded = status.Succeeded,
            Skipped = status.Skipped,
            Failed = status.Failed,
            Deleted = status.Deleted,
            BytesDeleted = status.BytesDeleted,
            DeletionAbortReason = status.DeletionAbortReason,
        });
    }

    private void Warn(string message) => Publish(new EngineWarningEvent
    {
        AtUtc = time.GetUtcNow(),
        Message = message,
    });

    /// <summary>Publishes, swallowing a throwing subscriber. Observability is decoration: it must never
    /// change what a run does, least of all a run that is about to delete something.</summary>
    private void Publish(EngineEvent engineEvent)
    {
        try
        {
            eventBus.Publish(engineEvent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing {Event} for a run failed", engineEvent.GetType().Name);
        }
    }

    /// <summary>One run's mutable state. Everything mutable is guarded by <see cref="Gate"/>; the
    /// counters are touched from the orchestrator's worker threads (via <c>Settled</c>) as well as the
    /// run's own task.</summary>
    private sealed class RunState(Guid runId, Profile profile, string? scopePath)
    {
        public Guid RunId { get; } = runId;
        public Profile Profile { get; } = profile;
        public string? ScopePath { get; } = scopePath;
        public object Gate { get; } = new();
        public CancellationTokenSource Cancel { get; } = new();

        public Task? Work { get; set; }
        public string? Directory { get; set; }

        public RunPhase Phase { get; set; } = RunPhase.Planning;
        public RunOutcome Outcome { get; set; } = RunOutcome.None;
        public bool Cancelled { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }

        public int PlannedCopies { get; set; }
        public int PlannedDeletes { get; set; }
        public long PlannedCopyBytes { get; set; }
        public long PlannedDeleteBytes { get; set; }
        public bool PlanTruncated { get; set; }
        public string? SweepFaultDetail { get; set; }

        /// <summary>Source entries the walk could not read. Non-zero means the plan covers a PARTIAL
        /// source tree while looking complete, which is enough to refuse a deletion phase.</summary>
        public int UnreadableSourceEntries { get; set; }

        public string? PlanError { get; set; }

        /// <summary>The §4.1 issues the planned profile raises, computed once when planning finishes.
        /// Null until then. Cached rather than recomputed so the list the user acknowledged on the
        /// approval footer is the same list that gates the approval.</summary>
        public IReadOnlyList<ValidationIssue>? ValidationIssues { get; set; }

        /// <summary>Payloads enqueued that are expected to produce a job. Decremented when one is
        /// superseded in the queue.</summary>
        public int Expected { get; set; }
        public int Settled { get; set; }
        public bool EnqueueComplete { get; set; }
        public bool BarrierTimedOut { get; set; }
        public long LastSettleTicks { get; set; }

        public int Succeeded { get; set; }
        public int SkippedJobs { get; set; }
        public int Failed { get; set; }

        /// <summary>Payloads that settled the barrier without producing an outcome — the orchestrator
        /// dropped them before the executor ran. Not a job failure, so it stays out of
        /// <see cref="Failed"/> and out of the counts the UI reports; but it IS work from the approved
        /// list that did not happen, so the Mirror deletion gate has to read it or the pass proceeds
        /// believing every copy landed.</summary>
        public int Dropped { get; set; }

        public int Deleted { get; set; }
        public long BytesDeleted { get; set; }
        public string? DeletionAbortReason { get; set; }
        public List<string> DeletionFailures { get; } = [];

        /// <summary>Every destination path this run's jobs actually resolved — the deletion pass's
        /// self-write guard.</summary>
        public HashSet<string> PathsWritten { get; } = new(StringComparer.OrdinalIgnoreCase);

        public RunStatus Snapshot()
        {
            lock (Gate)
            {
                return new RunStatus
                {
                    RunId = RunId,
                    ProfileId = Profile.Id,
                    Phase = Phase,
                    Outcome = Outcome,
                    PlannedCopies = PlannedCopies,
                    PlannedDeletes = PlannedDeletes,
                    PlannedCopyBytes = PlannedCopyBytes,
                    PlannedDeleteBytes = PlannedDeleteBytes,
                    Succeeded = Succeeded,
                    Skipped = SkippedJobs,
                    Failed = Failed,
                    Deleted = Deleted,
                    BytesDeleted = BytesDeleted,
                    PlanTruncated = PlanTruncated,
                    DeletionAbortReason = DeletionAbortReason,
                    PlanError = PlanError,
                    DeletionFailures = [.. DeletionFailures],
                };
            }
        }
    }
}
