using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <para><b>Runs are individually pausable</b> (<see cref="IRunPauseGate"/>), and a pause only ever
/// withholds work that has not started — it never aborts anything, which is what separates it from the
/// global engine pause. Three places observe it: the planning walk, the trigger queue's dequeue filter,
/// and the Mirror deletion pass. The paused interval is also SUBTRACTED from the drain barrier's deadline,
/// without which a run paused for longer than <c>MirrorBarrierTimeout</c> would time out and have its
/// deletion phase refused for an accounting artifact rather than a safety reason.</para></summary>
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
    RunPauseRegistry runPause,
    ISettingsProvider settings,
    ILogger<RunCoordinator> logger) : IRunCoordinator
{
    /// <summary>How often a paused run re-checks whether it may proceed. Only ever waited on by a run the
    /// user deliberately paused, so a coarse poll costs nothing and keeps the gate a readable loop rather
    /// than a per-run wait handle — the same trade <see cref="DrainPollInterval"/> makes.</summary>
    private static readonly TimeSpan PausePollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How many of a run's deletion failures survive its close. The list is bounded only by the
    /// orphan count, and a retained run now lives until it is discarded — so a pass that failed on tens of
    /// thousands of orphans would otherwise hold a string per orphan indefinitely. A hundred is far more
    /// than anyone reads and enough to characterize what went wrong.</summary>
    private const int MaxReportedDeletionFailures = 100;

    /// <summary>How often the drain loop re-checks the barrier. A run takes seconds to minutes, so this
    /// costs nothing and keeps the barrier logic a readable loop rather than a web of callbacks.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How often the idle sweep runs.
    ///
    /// <para>A fixed interval, NOT a fraction of the retention window. It used to be half the window,
    /// which was reasonable at ten minutes and absurd at a day — a 12-hour sweep would leave an
    /// auto-delete setting looking broken for half a day, and would let the count backstop go
    /// unenforced for just as long.</para></summary>
    private static readonly TimeSpan PruneSweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>Minimum spacing between planning-progress samples. Matches the ~10 frames/sec the dry-run
    /// stream used, which was measured to be enough for a caption to look live without the publish itself
    /// becoming the scan's bottleneck.</summary>
    private static readonly TimeSpan PlanProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a planning stage may stay silent while its counters do not move.
    ///
    /// <para>A sample that repeats the last one is noise at the poll rate, but total silence is worse: the
    /// counts freeze for the WHOLE of the Building stage, and a client that opens the queue window or
    /// reconciles during it is seeded from <c>get-runs</c>, which carries no scan figures at all. With
    /// nothing republished it reads "starting the scan" until the run is planned — the exact complaint the
    /// live counts were added to answer. Two seconds is quiet without being mute.</para></summary>
    private static readonly TimeSpan PlanRepublishInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    private readonly CancellationTokenSource _shutdown = new();


    /// <summary>Caps how many runs may be WALKING at once. Held for the duration of the plan walk only —
    /// released the moment the work list is frozen, because a run parked awaiting approval is doing no work
    /// and holding a slot for it would deadlock the queue once
    /// <see cref="EngineConfig.MaxConcurrentPlans"/> previews were on screen.</summary>
    private readonly SemaphoreSlim _planSlots = new(config.MaxConcurrentPlans, config.MaxConcurrentPlans);

    /// <summary>Drains closed runs while the service is idle. Lazily created on the first
    /// <see cref="Begin"/>, so a host that never runs anything never arms a timer, and disposed by
    /// <see cref="StopAsync"/>.</summary>
    private ITimer? _pruneTimer;
    private readonly object _pruneGate = new();

    public Result<RunHandle, string> Begin(Profile profile, string? scopePath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_shutdown.IsCancellationRequested)
            return "the service is shutting down";

        RunState run = new(Guid.NewGuid(), profile, scopePath, time.GetUtcNow());
        _runs[run.RunId] = run;
        PruneClosedRuns();
        ArmPruneTimer();

        // ANNOUNCE THE RUN NOW — synchronously, before the planning task is even scheduled.
        //
        // A run used to publish nothing until it either blocked on a plan slot or its walk crossed a 100 ms
        // progress boundary, and a walk that has not yielded its first chunk crosses no boundary at all. So
        // a run over a slow share existed for seconds with every client's job queue showing nothing —
        // precisely when a user opens the queue to see what is happening.
        //
        // Here rather than at the top of PlanAsync because that body runs on the thread pool: the
        // announcement would then be subject to scheduling delay, which is worst exactly when the machine is
        // busy. Publishing before the Task.Run also means the event cannot lose a race with the run-profile
        // reply — a client learns the run exists no later than it learns its id.
        //
        // The counters are zero, which is the honest picture: the run exists and has looked at nothing.
        PublishPlanningProgress(run, new DryRunProgressCounters(), RunPlanStages.Scanning);

        // Planning runs detached so the IPC caller is not held behind a scan (§8 rule 5). The token here is
        // None deliberately — cancelling a Task.Run's token only prevents the delegate from being
        // SCHEDULED, which would leave the run parked in Planning with nothing to close it. Stopping a walk
        // already in progress is the job of the linked CTS inside PlanAsync, which observes both the
        // coordinator's shutdown token and this run's own.
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
        runPause.Forget(run.RunId);
        lock (run.Gate)
        {
            run.Phase = RunPhase.Closed;
            run.Outcome = RunOutcome.Cancelled;
            run.ClosedAt = time.GetUtcNow();
            run.ReleaseAfterClose();
        }
        PublishCompleted(run);
        // Decline and cancel-while-parked come through here, and both used to close a run WITHOUT ever
        // sweeping. Those are the paths the GUI takes on every superseded preview, so with a day-scale
        // retention window they were the ones most likely to accumulate.
        PruneClosedRuns();
        error = null;
        return true;
    }

    public Result Cancel(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return $"no run with id {runId}";
        // BEFORE the token is fired, not after, and that ordering is load-bearing. Firing first leaves a
        // window in which the cancelled task has already unwound and reached its close — where it reads this
        // very flag to decide whether it was cancelled or whether it FAILED — while this method has not set
        // it yet, so the run closes as PlanFailed and the user is told their own cancel was an error. The
        // window is microseconds and a real walk takes far longer than that to unwind, which is why it never
        // showed; a planner that stops instantly loses the race about half the time.
        lock (run.Gate)
            run.Cancelled = true;
        // Null once the run has closed — ReleaseAfterClose disposes it. Cancelling an already-closed run is
        // a normal race (the queue's Cancel button against a run that just finished), not an error, so the
        // rest of this method still runs harmlessly and reports success.
        run.Cancel?.Cancel();
        // CANCEL SUPERSEDES PAUSE, and this line is load-bearing rather than tidy-up. A paused run's
        // remaining work is dropped below and its deletion phase is skipped, so the pause has nothing left
        // to withhold — but the drain loop treats a paused run as never quiescent (deliberately, so the
        // destructive phase cannot start behind the user's back). Leaving the flag set therefore parks a
        // cancelled run in Executing until the 30-minute barrier deadline expires: it never reaches Closed,
        // never cleans up its snapshot, and never leaves the queue. Found by
        // RunPauseTests.A_paused_run_is_never_treated_as_drained_by_the_quiescence_backstop, whose teardown
        // hung on exactly this.
        //
        // Forget rather than resume: it does not notify, so a cancelled run cannot emit a phantom wake
        // telling the trigger queue to look for work that has just been dropped.
        runPause.Forget(runId);
        // Work not yet started need not happen. Work already in flight is never interrupted
        // (I-ATOMIC-JOB) — the drain loop waits for it, and the deletion phase is skipped.
        int dropped = queue.DropRun(runId);
        logger.LogInformation("Run {RunId} cancelled; dropped {Dropped} pending payload(s)", runId, dropped);

        // A run already PARKED for approval has nobody left to notice the cancel: PlanAsync has returned,
        // and ExecuteAsync only runs on approval. So close it here, or it sits in AwaitingApproval forever
        // — ClosedAt never set, so PruneClosedRuns never reaps it and CleanUpSnapshot never runs, leaking
        // its snapshot directory (plan.json plus four ndjsonl files, up to two rows per scanned file) for
        // the lifetime of the service. Cancelling at exactly the wrong moment was enough to do it, and
        // repeating that filled %LOCALAPPDATA% with directories nothing reclaims.
        //
        // The phase is re-checked inside CloseUnstarted under the gate, so losing the race with an
        // approval that got there first is a no-op rather than a double close.
        CloseUnstarted(run, RunPhase.AwaitingApproval, out _);
        return Result.Success();
    }

    public Result Discard(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return $"no run with id {runId}";

        bool live;
        lock (run.Gate)
            live = run.Phase != RunPhase.Closed;

        // A live run is CANCELLED first — discard means "I am done with this", and abandoning a run
        // mid-flight without stopping it would leave work running that nothing is tracking. Cancel's own
        // semantics still hold: pending work is dropped, jobs already in flight finish (I-ATOMIC-JOB), and
        // a Mirror run's deletion phase is skipped. So the ROW disappears immediately; the last in-flight
        // file does not.
        if (live)
            Cancel(runId);

        // Belt and braces after the cancel: an Executing run's Close is still unwinding on its own task and
        // will run these again harmlessly, while a run cancelled from Planning may not have reached them
        // yet. Both are idempotent — CleanUpSnapshot no-ops on a null Directory, Forget on a missing key.
        CleanUpSnapshot(run);
        runPause.Forget(runId);

        // THE removal. Until this existed, the age-based sweep was the only thing that ever took an entry
        // out of _runs — so a user looking at a finished run had no way to be rid of it, and turning
        // auto-delete off would have meant the queue only ever grew.
        _runs.TryRemove(runId, out _);

        // Note what is deliberately NOT done: a discarded Executing run's ExecuteAsync keeps unwinding
        // against a RunState no longer in _runs. Its eventual Close publishes a run-completed for a run no
        // client is listing, and Settled early-returns on the unknown id. Harmless, and cheaper than
        // teaching every path to check whether it has been forgotten mid-flight.
        logger.LogInformation("Run {RunId} discarded{State}", runId, live ? " (cancelled first)" : "");
        return Result.Success();
    }

    public Result SetPaused(Guid runId, bool paused)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
            return $"no run with id {runId}";

        lock (run.Gate)
        {
            // A closed run cannot be paused, and saying so beats silently accepting it: the caller's UI
            // would otherwise show a paused row for a run that has already finished.
            if (run.Phase == RunPhase.Closed)
                return $"run {runId} has already finished";
        }

        // The registry is the source of truth for the FLAG (the trigger queue reads it on every dequeue);
        // this method owns the accounting a transition implies. Set reports whether anything changed, so
        // pausing an already-paused run stays idempotent rather than announcing a phantom transition.
        if (!runPause.Set(runId, paused))
            return Result.Success();

        lock (run.Gate)
        {
            // The drain barrier's deadline must not run while the user is holding the run. Started on
            // pause, folded into PausedElapsed on resume — see AwaitDrainAsync.
            if (paused)
                run.PausedSinceTicks = time.GetTimestamp();
            else if (run.PausedSinceTicks is { } since)
            {
                run.PausedElapsed += time.GetElapsedTime(since);
                run.PausedSinceTicks = null;
            }
        }

        logger.LogInformation("Run {RunId} was {State}", runId, paused ? "paused" : "resumed");
        // So a paused run keeps saying so rather than looking wedged — the counters stop moving, and this
        // sample is the only thing that distinguishes the two.
        PublishProgress(run);
        return Result.Success();
    }

    /// <summary>Blocks while this run is paused. Returns when it resumes, is cancelled, or the service is
    /// shutting down — never throws for a pause, only for the cancellation the caller already handles.
    /// <para>Polls rather than waiting on a handle: only a deliberately paused run ever gets here, and the
    /// caller is a loop that must also observe cancellation, which a poll gives for free.</para></summary>
    private async Task WaitWhileRunPausedAsync(RunState run, CancellationToken ct)
    {
        while (runPause.IsRunPaused(run.RunId) && !run.Cancelled && !ct.IsCancellationRequested)
            await Task.Delay(PausePollInterval, ct).ConfigureAwait(false);
    }

    public RunStatus? GetStatus(Guid runId) =>
        _runs.TryGetValue(runId, out RunState? run) ? run.Snapshot() : null;

    public IReadOnlyList<RunSummaryDto> ListRuns()
    {
        List<RunSummaryDto> runs = [];
        // ConcurrentDictionary enumeration is a moving snapshot, which is exactly right here: a run that
        // starts or is pruned mid-enumeration simply is or is not in this answer, and the caller re-seeds
        // from the event stream either way.
        foreach (RunState run in _runs.Values)
            runs.Add(run.Summarize(runPause.IsRunPaused(run.RunId)));
        // Newest first, matching get-recent-jobs, so a client can prepend without re-sorting. StartedAt is
        // monotonic per run because Begin stamps it before publishing anything.
        runs.Sort(static (a, b) => b.StartedAtUtc.CompareTo(a.StartedAtUtc));
        return runs;
    }

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
            // Before the outcome switch, and outside it: the byte numerator must track the FILE
            // numerator below (Succeeded + Skipped + Failed), so it is added on every outcome. A null
            // completion — a payload the orchestrator dropped — has no size to read and adds nothing,
            // which matches run.Dropped staying out of the file numerator too.
            run.BytesSettled += completion?.SourceBytes ?? 0;
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
            // MIRROR ONLY. This set exists solely as the deletion pass's self-write guard, and nothing
            // else ever reads it — so an additive profile was paying one retained string per copied file
            // (megabytes on a large run) to build a set no code path would ever look at. Now that a
            // finished run is retained until discarded rather than for ten minutes, that stopped being
            // merely wasteful.
            if (completion is not null && run.Profile.SyncMode == SyncMode.Mirror)
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
        ITimer? prune;
        lock (_pruneGate)
        {
            prune = _pruneTimer;
            _pruneTimer = null;
        }
        if (prune is not null)
            await prune.DisposeAsync().ConfigureAwait(false);
        List<Task> pending = [];
        foreach (RunState run in _runs.Values)
        {
            // Both null for a run that has already closed — ReleaseAfterClose disposes the source and drops
            // the task. Nothing to cancel and nothing to await; only live runs need either.
            run.Cancel?.Cancel();
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
        // After the runs have unwound: a run still queued on a plan slot is released by _shutdown
        // cancelling its WaitAsync, and disposing the semaphore while one waits would throw there instead.
        _planSlots.Dispose();
        _shutdown.Dispose();
    }

    // ---- planning ----------------------------------------------------------------------------------

    private async Task PlanAsync(RunState run)
    {
        // Non-null here by construction: PlanAsync is started by Begin, and only a close releases the
        // source — which cannot have happened to a run that has not planned yet.
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, run.Cancel!.Token);
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
        DateTimeOffset? plannedAt = null;
        bool slotHeld = false;

        try
        {
            // Bounded concurrency. Several previews at once is the point of the job queue, but planning is
            // the memory-hungry half of a run — the service measured ~292 MB producing ONE 33k-file plan,
            // so an unbounded fan-out of large profiles exhausts it. The run stays in RunPhase.Planning
            // while queued and reports the Waiting phase, so a client can say "waiting" instead of showing
            // a scan stuck at zero.
            //
            // Taken BEFORE the writer is constructed: a queued run should not be holding an open snapshot
            // file, and the directory it would create is cleaned up on the failure path either way.
            if (!_planSlots.Wait(0))
            {
                lock (run.Gate)
                    run.WaitingToPlan = true;
                // So the wait is visible, not silent. This is now the ONLY deliberately quiet window left in
                // planning: while parked here the counters cannot move, and the row reports Waiting rather
                // than a scan stuck at zero, which is the honest picture.
                PublishPlanningProgress(run, counters, RunPlanStages.Scanning);
                await _planSlots.WaitAsync(linked.Token).ConfigureAwait(false);
                lock (run.Gate)
                    run.WaitingToPlan = false;
            }
            slotHeld = true;

            using RunSnapshotWriter writer = new(directory, logger);
            // Real time, not the injected clock, and for the same reason the poll heartbeat below is: the
            // two intervals gate the same samples, so a run driven by a FakeTimeProvider would otherwise
            // have a throttle that never elapses and a heartbeat that always does — every between-chunk
            // sample suppressed for the life of the run. Nothing is DECIDED from this; it only spaces a
            // display feed. Seeded with a real timestamp, never left at 0: GetElapsedTime(0) reads as
            // machine uptime, which would make both intervals meaningless.
            long lastProgress = Stopwatch.GetTimestamp();
            // What the last published sample SAID. Seeded to the zero pair Begin has already announced, so
            // the first sample this loop emits is one that actually moved — a scan that has found nothing
            // yet re-states nothing.
            long lastSources = 0, lastDestinations = 0, lastSkipped = 0;
            // Which part of planning the counters should be read as. Derived from the chunks themselves: a
            // Sources chunk cannot exist until the walk has finished (see below), and the planner announces
            // the sweep with a zero-entry marker.
            string stage = RunPlanStages.Scanning, lastStage = RunPlanStages.Scanning;

            // Publishes a sample only when it would SAY something new — with a floor, because "nothing new"
            // is not the same as "nothing worth saying". Three gates:
            //
            // A count that has not moved is worth little to a reader already watching, so a stalled phase
            // goes quiet rather than repeating itself ten times a second at every connected client. Quiet,
            // not mute: a reader who was NOT already watching has nothing at all — get-runs carries no scan
            // figures — so an unchanged sample is republished once per PlanRepublishInterval. That window
            // is what bounds how long a queue opened during the Building stage, whose counts are frozen by
            // construction, can read "starting the scan". It also heals the zeroed scan fields an execution
            // progress sample (SetPaused's) writes over a planning row.
            //
            // Between chunks the shorter elapsed check bounds a tree that yields hundreds of small ones. A
            // STAGE change bypasses both time gates — it is exactly the news the counters cannot carry, and
            // it arrives precisely when they have stopped moving.
            void SampleProgress(bool throttleByTime)
            {
                bool stageChanged = stage != lastStage;
                bool moved = counters.Sources != lastSources
                    || counters.Destinations != lastDestinations
                    || counters.Skipped != lastSkipped;
                TimeSpan since = Stopwatch.GetElapsedTime(lastProgress);
                if (!stageChanged && !moved && since < PlanRepublishInterval)
                    return;
                if (throttleByTime && !stageChanged && since < PlanProgressInterval)
                    return;
                (lastSources, lastDestinations, lastSkipped, lastStage) =
                    (counters.Sources, counters.Destinations, counters.Skipped, stage);
                lastProgress = Stopwatch.GetTimestamp();
                PublishPlanningProgress(run, counters, stage);
            }

            // The plan's own cancellation authority over the enumerator, cancelled only if this loop is left
            // with an advance still in flight. Distinct from `linked` deliberately: the teardown helper
            // CANCELS what it is handed, and `linked` still has to survive the snapshot completion below and
            // the disposal ordering on the failure path, which is the one ordering here that has ever been
            // fragile.
            using CancellationTokenSource planCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);

            // Driven through an explicit enumerator rather than `await foreach`, for one reason: the
            // planner's ENTIRE source walk happens inside the FIRST MoveNextAsync — the engine fuses scan
            // and evaluate and spools every finding before a chunk exists — so a foreach publishes nothing
            // at all for the longest part of a run, and the queue row read "starting the scan" for minutes
            // over a slow share. The counters are already live throughout (the scan pump and the sweep's
            // walkers increment them); this samples them WHILE the advance is pending. The same dance, for
            // the same reason, as DryRunStreamHandler — and, because it runs on the run's own task, a
            // planning sample can never land after the run-planned event that follows this loop.
            await using (IAsyncEnumerator<Result<PlanChunk, string>> chunks = planner
                .PlanAsync(run.Profile, run.ScopePath, state, counters, planCts.Token)
                .GetAsyncEnumerator(planCts.Token))
            {
                Task<bool> advance = chunks.MoveNextAsync().AsTask();
                try
                {
                    while (true)
                    {
                        // Unthrottled by the between-chunk gate: the heartbeat's own delay IS the throttle
                        // here, and applying the shorter one on top would only ever suppress a sample the
                        // poll had already spaced correctly.
                        while (await AsyncIteratorHeartbeat
                            .WaitForTickAsync(advance, PlanProgressInterval, linked.Token)
                            .ConfigureAwait(false))
                            SampleProgress(throttleByTime: false);

                        // Propagates plan faults and cancellation exactly as the foreach did.
                        if (!await advance.ConfigureAwait(false))
                            break;
                        Result<PlanChunk, string> chunk = chunks.Current;

                        if (chunk.TryGetError(out string? error))
                        {
                            failure = error;
                            break;
                        }
                        chunk.TryGetValue(out PlanChunk planned);
                        // A chunk from the source phase is proof the walk is over: the engine cannot produce
                        // one until its scan has completed and it is replaying what it found. That makes this
                        // the exact moment the two scan counts freeze — and the reason the stage has to be
                        // published at all, since from the counters alone that is indistinguishable from a
                        // walk that has wedged. The sweep announces itself with its own zero-entry marker.
                        stage = planned.Phase == PlanPhase.Destinations
                            ? RunPlanStages.Sweeping
                            : RunPlanStages.Building;
                        writer.Consume(planned, run.Profile);
                        // Between chunks, never mid-chunk: the writer has just consumed a complete chunk, so
                        // this is a point at which the snapshot on disk is coherent and the enumerator holds
                        // no half-read directory. Pausing inside the planner would mean holding scan-scheduler
                        // slots and directory handles open for as long as the user cared to wait.
                        if (runPause.IsRunPaused(run.RunId))
                        {
                            PublishPlanningProgress(run, counters, stage);   // say so before going quiet
                            await WaitWhileRunPausedAsync(run, linked.Token).ConfigureAwait(false);
                        }
                        // Still sampled between chunks as well as during an advance: the destination sweep
                        // STREAMS, so its advances complete too fast for the poll above to see, and its
                        // counts would otherwise only ever be published once the sweep was over.
                        SampleProgress(throttleByTime: true);

                        advance = chunks.MoveNextAsync().AsTask();
                    }
                }
                finally
                {
                    // Ordered before the await-using's dispose: it must never run against an in-flight
                    // MoveNextAsync. Unreachable on every normal path here — each break above falls into the
                    // await that consumes the advance — so this is a completed-task no-op that cancels
                    // nothing. Kept because it is the one copy of this teardown, shared with ProfilePlanner
                    // and DryRunStreamHandler, and because "unreachable" is a property of the loop body that
                    // an edit could quietly take away.
                    await AsyncIteratorTeardown.ObserveAbandonedAdvanceAsync(advance, planCts, logger)
                        .ConfigureAwait(false);
                }
            }

            // One last sample, unthrottled: a sweep's closing chunks routinely all arrive inside a single
            // throttle window, and the time gate would then swallow the counts they carried — leaving the
            // row settled on a figure short of what was actually found, for as long as the plan sits waiting
            // to be approved. Still change-gated, so a plan that ended on a published count adds nothing.
            SampleProgress(throttleByTime: false);

            if (failure is null && writer.Failure is { } writeFailure)
                failure = writeFailure;

            if (failure is null)
            {
                plannedAt = time.GetUtcNow();
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
                    // The same instant reported to clients as RunSummaryDto.PlannedAtUtc, not a second
                    // call: a client measures a stored preview's staleness against that value, and the
                    // snapshot is what it is measuring the age OF.
                    PlannedAtUtc = plannedAt.Value,
                    CopyItemCount = writer.CopyCount,
                    DeleteItemCount = writer.DeleteCount,
                    CopyBytes = writer.CopyBytes,
                    DeleteBytes = writer.DeleteBytes,
                    SourceItemCount = writer.SourceCount,
                    DestinationItemCount = writer.DestinationCount,
                    OverwriteCount = writer.OverwriteCount,
                    RenameCount = writer.RenameCount,
                    DisposalCount = writer.DisposalCount,
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
            // Cancel is non-null here — only a close releases it, and this run has not closed yet — but
            // read defensively rather than asserting: getting it wrong turns a cancellation into an
            // unobserved NRE on a detached task.
            failure = run.Cancel?.IsCancellationRequested == true
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
        finally
        {
            // Released as soon as the WALK is over, not when the run closes: a run parked in
            // AwaitingApproval is doing no work and must not hold a planning slot — with previews now
            // retained per profile, several parked runs are the normal state, and holding slots for them
            // would deadlock the queue after MaxConcurrentPlans previews.
            if (slotHeld)
                _planSlots.Release();
            lock (run.Gate)
            {
                run.WaitingToPlan = false;
                run.PlannedAt = plannedAt;
            }
        }

        if (failure is not null || run.Cancelled)
        {
            // Dispose the LINKED source before the close below disposes the one it was derived from.
            // ReleaseAfterClose disposes run.Cancel, and a child outliving its parent is the one ordering
            // that has ever been fragile here. Double disposal is a documented no-op, so the `using` at
            // the top of this method is free to run again.
            linked.Dispose();
            CleanUpSnapshot(run);   // before Closed is observable — see Close()
            runPause.Forget(run.RunId);
            lock (run.Gate)
            {
                run.Phase = RunPhase.Closed;
                run.Outcome = run.Cancelled ? RunOutcome.Cancelled : RunOutcome.PlanFailed;
                run.PlanError = failure;
                run.ClosedAt = time.GetUtcNow();
                run.ReleaseAfterClose();
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
            TimeSpan paused;
            lock (run.Gate)
            {
                // A paused run is not quiescent, however long it has been still. The PendingCountForRun
                // check below already covers the ordinary case — a paused run's payloads stay pending —
                // but not the moment after its last payload was dequeued, where nothing is pending, the
                // in-flight job is finishing, and the settle clock has run past the window. Reading that
                // as "drained" would let the deletion phase start while the user believes the run is held.
                quiescent = !runPause.IsRunPaused(run.RunId)
                    && run.EnqueueComplete
                    && queue.PendingCountForRun(run.RunId) == 0
                    && time.GetElapsedTime(run.LastSettleTicks) > config.MirrorQuiescenceWindow
                    && run.Settled < run.Expected;
                // Includes the pause in progress, so the deadline stays frozen for its whole duration
                // rather than only catching up when the user resumes.
                paused = run.PausedElapsed
                    + (run.PausedSinceTicks is { } since ? time.GetElapsedTime(since) : TimeSpan.Zero);
            }
            if (quiescent)
            {
                logger.LogWarning(
                    "Run {RunId}: {Settled} of {Expected} payload(s) reported settling, but nothing is queued " +
                    "and nothing has settled for {Window} — treating the run as drained",
                    run.RunId, run.Settled, run.Expected, config.MirrorQuiescenceWindow);
                return;
            }

            // Minus the paused interval: the deadline measures how long the WORK has been unaccounted for,
            // not how long the run object has existed. A user holding a run must not be able to talk it
            // into a timeout.
            if (time.GetElapsedTime(started) - paused >= config.MirrorBarrierTimeout)
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
            // Counted separately from the list's length, which the close truncates — see
            // DeletionFailuresTotal.
            run.DeletionFailuresTotal += result.Failures.Count;
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
        // Also before Closed becomes observable, and for the same reason: a closed run must not read as
        // paused. Forget rather than resume — it does not notify, so a run cancelled while paused cannot
        // produce a phantom wake telling the queue to look for work that no longer exists.
        runPause.Forget(run.RunId);
        lock (run.Gate)
        {
            run.Phase = RunPhase.Closed;
            run.Outcome = outcome;
            run.PlanError ??= planError;
            run.ClosedAt = time.GetUtcNow();
            run.ReleaseAfterClose();
        }
        PublishCompleted(run);
        logger.LogInformation(
            "Run {RunId} closed {Outcome}: {Succeeded} succeeded, {Skipped} skipped, {Failed} failed, " +
            "{Dropped} dropped, {Deleted} deleted",
            run.RunId, outcome, run.Succeeded, run.SkippedJobs, run.Failed, run.Dropped, run.Deleted);
        // This run is not eligible yet, but its predecessors may be — and a service whose last run has just
        // finished may never call Begin again.
        PruneClosedRuns();
    }

    /// <summary>Drops the run's frozen work list once the run is over. Best-effort: a failure here must
    /// never affect an outcome, since the run's real record is the journal and the audit trail.
    /// <para>A swallowed failure — a locked file, typically — leaves the directory for
    /// <c>EngineHost.PurgeRunSnapshots</c> to collect on the next start. The warning below is the only
    /// trace until then.</para></summary>
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

    /// <summary>Auto-deletes finished runs, and enforces the count backstop. Two independent rules.
    ///
    /// <para><b>1. Age, only when the user asked for it.</b> A finished run is the record of what the
    /// engine did; deleting one is a decision. So a run is removed on age ONLY while
    /// <see cref="GlobalSettings.AutoDeleteFinishedRuns"/> is on, and everything else that leaves the
    /// queue leaves because <see cref="Discard"/> was called. This replaces a hard-coded ten-minute
    /// window that deleted a run's history out from under whoever was reading it.</para>
    ///
    /// <para><b>2. Count, always.</b> Auto-delete can be switched off entirely, which without a second
    /// rule would make "never delete" an unbounded allocation. <c>MaxRetainedClosedRuns</c> caps the
    /// retained set regardless, evicting the oldest-closed first — a backstop rather than a knob, and set
    /// far above any hand-driven session. What makes it affordable to sit near that cap is
    /// <c>RunState.ReleaseAfterClose</c>: a closed run is a handful of scalars, not a profile plus a
    /// string per copied file.</para>
    ///
    /// <para>Settings are read on every sweep, not captured, so changing either takes effect on the next
    /// tick rather than at the next restart.</para></summary>
    private void PruneClosedRuns()
    {
        GlobalSettings current = settings.Current;

        if (current.AutoDeleteFinishedRuns)
        {
            DateTimeOffset cutoff = time.GetUtcNow() - current.FinishedRunRetention;
            foreach ((Guid id, RunState run) in _runs)
                if (run.ClosedAt is { } closedAt && closedAt < cutoff)
                    _runs.TryRemove(id, out _);
        }

        // The backstop. Counted first so the ordinary case — comfortably under the cap — costs one pass
        // and no allocation at all.
        int closedCount = 0;
        foreach (RunState run in _runs.Values)
            if (run.ClosedAt is not null)
                closedCount++;
        if (closedCount <= config.MaxRetainedClosedRuns)
            return;

        // Oldest-closed first, so what survives is what the user is most likely to still care about. A
        // LIVE run is never a candidate however long it has been going: it is not history yet, and
        // removing it would strand work nothing is tracking.
        List<(DateTimeOffset ClosedAt, Guid Id)> closed = new(closedCount);
        foreach ((Guid id, RunState run) in _runs)
            if (run.ClosedAt is { } at)
                closed.Add((at, id));
        closed.Sort(static (a, b) => a.ClosedAt.CompareTo(b.ClosedAt));

        int excess = closed.Count - config.MaxRetainedClosedRuns;
        for (int i = 0; i < excess; i++)
            _runs.TryRemove(closed[i].Id, out _);
        logger.LogInformation(
            "Retained-run cap reached: forgot the {Excess} oldest finished run(s), keeping {Kept}",
            excess, config.MaxRetainedClosedRuns);
    }

    /// <summary>Starts the idle sweep once, on the first run, at a FIXED interval — see
    /// <see cref="PruneSweepInterval"/> for why it is no longer derived from the retention window.</summary>
    private void ArmPruneTimer()
    {
        if (_pruneTimer is not null)
            return;
        lock (_pruneGate)
        {
            if (_pruneTimer is not null || _shutdown.IsCancellationRequested)
                return;
            _pruneTimer = time.CreateTimer(
                static state => ((RunCoordinator)state!).PruneOnTimer(), this,
                PruneSweepInterval, PruneSweepInterval);
        }
    }

    private void PruneOnTimer()
    {
        try
        {
            PruneClosedRuns();
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: this runs on a timer thread with no caller to observe a throw,
            // and failing to prune must never take the service down.
            logger.LogDebug(ex, "Pruning closed runs on the idle timer failed");
        }
    }

    private void PublishPlanned(RunState run, string? error) => Publish(new RunPlannedEvent
    {
        AtUtc = time.GetUtcNow(),
        RunId = run.RunId,
        ProfileId = run.Profile.Id,
        // From the FROZEN profile, which is the only thing that can answer: a run planned from an unsaved
        // draft has a profile that is in no catalog, so a client's own lookup returns null for exactly the
        // runs the GUI starts.
        ProfileName = run.Profile.Name,
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
    /// <param name="stage">Which part of planning this sample describes, from <see cref="RunPlanStages"/>.
    /// Carried because the counts alone cannot distinguish the stretch where the walk is over and its
    /// findings are being written — both frozen — from a walk that has wedged.</param>
    private void PublishPlanningProgress(RunState run, DryRunProgressCounters counters, string stage)
    {
        bool paused = runPause.IsRunPaused(run.RunId);
        bool waiting;
        lock (run.Gate)
            waiting = run.WaitingToPlan;
        Publish(new RunProgressEvent
        {
            AtUtc = time.GetUtcNow(),
            RunId = run.RunId,
            ProfileId = run.Profile.Id,
            ProfileName = run.Profile.Name,
            // WaitingPhase, not Planning, while queued behind the concurrent-plan limit. Both are
            // RunPhase.Planning to the engine; the distinction exists only because "scanning, 0 files
            // found" and "not started yet" look identical to a user and mean very different things.
            Phase = waiting ? WaitingPhase : RunPhase.Planning.ToString(),
            Completed = 0,
            Total = 0,
            // Zero for the same reason Total is: working out the denominator is what planning IS.
            CompletedBytes = 0,
            TotalBytes = 0,
            Deleted = 0,
            ScannedSources = counters.Sources,
            ScannedDestinations = counters.Destinations,
            // Said live as well as in the approval-time warning: a tree the walk is failing to read is worth
            // knowing about while it is being walked, not only once it is too late to stop it.
            UnreadableEntries = counters.Skipped,
            PlanStage = stage,
            Paused = paused,
        });
    }

    /// <summary>The <c>Phase</c> string a run reports while queued behind
    /// <see cref="EngineConfig.MaxConcurrentPlans"/>. Not a <see cref="RunPhase"/> member: the run really
    /// is in <c>Planning</c>, and adding a phase would ripple through the §7.1 transition tables,
    /// <see cref="RunStatus"/>, and every consumer of both to express a display distinction.</summary>
    public const string WaitingPhase = "Waiting";

    /// <summary>An execution progress sample.
    /// <para>Reads the counters directly rather than through <c>run.Snapshot()</c>, which allocates a whole
    /// <see cref="RunStatus"/> including a COPY of the deletion-failure list to hand back six integers. The
    /// drain loop polls five times a second, so a run that reaches the 30-minute deadline did that 9,000
    /// times — copying, late in a troubled run, thousands of strings per sample to publish a count.</para></summary>
    private void PublishProgress(RunState run)
    {
        RunPhase phase;
        int completed, total, deleted;
        long completedBytes, totalBytes;
        bool waiting;
        bool paused = runPause.IsRunPaused(run.RunId);
        lock (run.Gate)
        {
            phase = run.Phase;
            completed = run.Succeeded + run.SkippedJobs + run.Failed;
            total = run.PlannedCopies;
            completedBytes = run.BytesSettled;
            totalBytes = run.PlannedCopyBytes;
            deleted = run.Deleted;
            waiting = run.WaitingToPlan;
        }
        Publish(new RunProgressEvent
        {
            AtUtc = time.GetUtcNow(),
            RunId = run.RunId,
            ProfileId = run.Profile.Id,
            ProfileName = run.Profile.Name,
            Phase = waiting && phase == RunPhase.Planning ? WaitingPhase : phase.ToString(),
            Completed = completed,
            Total = total,
            // The byte pair, alongside the file pair rather than instead of it: a run is "3,412 of
            // 12,088 files" AND "1.2 GB of 4.5 GB", and which of those is the useful sentence depends
            // entirely on whether the files are photos or disk images.
            CompletedBytes = completedBytes,
            TotalBytes = totalBytes,
            Deleted = deleted,
            Paused = paused,
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
    private sealed class RunState(Guid runId, Profile profile, string? scopePath, DateTimeOffset startedAt)
    {
        public Guid RunId { get; } = runId;
        public Profile Profile { get; } = profile;
        public string? ScopePath { get; } = scopePath;
        public object Gate { get; } = new();

        /// <summary>The run's own cancellation source. <b>Null once the run has closed</b> — released and
        /// disposed by <see cref="ReleaseAfterClose"/>, so every reader must null-check. Before that
        /// change it was never disposed at all, anywhere.</summary>
        public CancellationTokenSource? Cancel { get; set; } = new();

        /// <summary>When the run was created. Immutable, and the queue's sort key — a run that has not
        /// finished planning has no <see cref="PlannedAt"/> and would otherwise have no position.</summary>
        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>When planning finished and the work list was frozen; null until then. The same value
        /// stamped into the snapshot header, so a client's staleness age and the snapshot agree.</summary>
        public DateTimeOffset? PlannedAt { get; set; }

        public Task? Work { get; set; }
        public string? Directory { get; set; }

        public RunPhase Phase { get; set; } = RunPhase.Planning;
        public RunOutcome Outcome { get; set; } = RunOutcome.None;
        public bool Cancelled { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }

        /// <summary>Timestamp the current pause began, or null when not paused. Folded into
        /// <see cref="PausedElapsed"/> on resume.</summary>
        public long? PausedSinceTicks { get; set; }

        /// <summary>Total time this run has spent paused across every pause, SUBTRACTED from the drain
        /// barrier's deadline.
        /// <para>Not bookkeeping for its own sake. The barrier's deadline is
        /// <c>MirrorBarrierTimeout</c> (30 minutes) and expiring it sets <see cref="BarrierTimedOut"/>,
        /// which refuses the Mirror deletion phase. Without this, pausing a run over a lunch break would
        /// make it come back and delete nothing — reported as unaccounted copies, which is a safety
        /// message about something that was never unsafe.</para></summary>
        public TimeSpan PausedElapsed { get; set; }

        /// <summary>Planning has started but is queued behind the concurrent-plan limit — no walking yet.
        /// Reported so the UI can say "waiting" rather than showing a scan at zero, which is
        /// indistinguishable from a wedged one.</summary>
        public bool WaitingToPlan { get; set; }

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

        /// <summary>Source bytes accounted for by every job of this run that has settled — the numerator
        /// for byte-level progress against <see cref="PlannedCopyBytes"/>.
        ///
        /// <para>Summed from <see cref="JobCompletion.SourceBytes"/> on every outcome, so it tracks
        /// <see cref="Succeeded"/> + <see cref="SkippedJobs"/> + <see cref="Failed"/> exactly. A
        /// <see cref="Dropped"/> payload contributes nothing (there is no completion to read a size
        /// from), which leaves the bar short of full on a run that dropped work — the same shortfall the
        /// file counters already show, and honest about it.</para></summary>
        public long BytesSettled { get; set; }

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

        /// <summary>How many deletion failures there were, which <see cref="DeletionFailures"/> may
        /// under-report once the run has closed.
        /// <para>A plain counter rather than the list's length because <see cref="ReleaseAfterClose"/>
        /// truncates the list to <see cref="MaxReportedDeletionFailures"/> — without this, a pass that
        /// failed on thousands of orphans reported exactly a hundred and looked indistinguishable from one
        /// that had a hundred.</para></summary>
        public int DeletionFailuresTotal { get; set; }

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
                    BytesSettled = BytesSettled,
                    Deleted = Deleted,
                    BytesDeleted = BytesDeleted,
                    PlanTruncated = PlanTruncated,
                    DeletionAbortReason = DeletionAbortReason,
                    PlanError = PlanError,
                    DeletionFailures = [.. DeletionFailures],
                    DeletionFailuresTotal = DeletionFailuresTotal,
                };
            }
        }

        /// <summary>The wire form, for <c>get-runs</c>. Separate from <see cref="Snapshot"/> rather than
        /// projected from it because the two answer different questions: <c>RunStatus</c> is the engine's
        /// own read model (and carries a copy of the deletion-failure list), while this is a display row
        /// and adds the profile name, the pause/waiting flags, and the two timestamps a queue needs.</summary>
        /// <param name="paused">Read from <see cref="RunPauseRegistry"/> by the caller — the flag lives
        /// there, not here, so the trigger queue can consult it without knowing about runs.</param>
        public RunSummaryDto Summarize(bool paused)
        {
            lock (Gate)
            {
                return new RunSummaryDto(
                    RunId, Profile.Id, Profile.Name,
                    // Waiting is a display distinction over Planning, not a phase — see
                    // RunCoordinator.WaitingPhase.
                    WaitingToPlan && Phase == RunPhase.Planning ? WaitingPhase : Phase.ToString(),
                    Outcome.ToString(),
                    paused, WaitingToPlan, StartedAt, PlannedAt, ClosedAt,
                    PlannedCopies, PlannedDeletes, PlannedCopyBytes, PlannedDeleteBytes,
                    Succeeded, SkippedJobs, Failed, Deleted, PlanTruncated, PlanError,
                    BytesSettled);
            }
        }

        /// <summary>Frees what a CLOSED run has no further use for. Called under <see cref="Gate"/> from
        /// every close path, immediately after the phase flips.
        ///
        /// <para><b>This is what makes long retention affordable.</b> A run used to be forgotten ten
        /// minutes after it closed, and that window was the only thing bounding what it held. Now a
        /// finished run stays until the user discards it — possibly forever, since auto-delete can be
        /// switched off — so it has to stop being expensive at the moment it stops being live. The heavy
        /// members below are each provably dead by close; what remains is the handful of scalars
        /// <see cref="Summarize"/> and <see cref="Snapshot"/> read.</para>
        ///
        /// <para>Deliberately NOT released: <see cref="Profile"/>. <c>PlannedProfile</c> can still be
        /// asked for it by a payload that outlived the run (a quiescence or deadline close leaves work
        /// queued), and <see cref="Summarize"/> needs its id and name. A profile record is bounded by its
        /// own configuration, unlike everything above it.</para></summary>
        public void ReleaseAfterClose()
        {
            // The unbounded one: one string per file this run copied, ~8-10 MB for a 33k-file run. Its
            // only consumer is DeleteOrphansAsync, which copies it into its own set before the deletion
            // pass runs — and the deletion pass finishes before Close is reached.
            PathsWritten.Clear();
            PathsWritten.TrimExcess();

            // Read only by Refusal and BlockingIssuesOf, both reachable only from AwaitingApproval. A
            // closed run cannot be approved, so nothing can ask again.
            ValidationIssues = null;

            // Roots the completed PlanAsync/ExecuteAsync state machine, and through it every local those
            // methods captured — the snapshot writer, the plan state, the counters. StopAsync only awaits
            // runs that are still unwinding, and this one has closed.
            Work = null;

            // Disposed here because there is nowhere else it ever was: before this, every run in the
            // process leaked its CTS. Nulled rather than left disposed so the two callers that may race a
            // close (StopAsync and Cancel) can null-check instead of catching ObjectDisposedException.
            CancellationTokenSource? cts = Cancel;
            Cancel = null;
            cts?.Dispose();

            // Bounded only by the orphan count, and Snapshot copies the whole list on every GetStatus.
            // DeletionFailuresTotal is deliberately NOT touched: it is what keeps a truncated list reporting
            // honestly rather than looking complete.
            if (DeletionFailures.Count > MaxReportedDeletionFailures)
                DeletionFailures.RemoveRange(
                    MaxReportedDeletionFailures, DeletionFailures.Count - MaxReportedDeletionFailures);
            DeletionFailures.TrimExcess();
        }
    }
}
