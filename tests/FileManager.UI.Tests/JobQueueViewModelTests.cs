using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.UI.Tests;

/// <summary>The job queue's rows: event fan-in, the lossy-stream re-seed, and the two rules that keep it
/// honest — a finished run stays finished, and Approve is offered only for a plan this window showed.</summary>
public sealed class JobQueueViewModelTests
{
    private static RunSummaryDto Summary(
        Guid runId, string phase = "Executing", string outcome = "None",
        bool paused = false, bool waiting = false, string name = "Photos",
        int plannedCopies = 10, int succeeded = 3, DateTimeOffset? closedAt = null) =>
        new(runId, Guid.NewGuid(), name, phase, outcome, paused, waiting,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, closedAt,
            plannedCopies, 0, 1024, 0, succeeded, 0, 0, 0, false, null);

    private static RunProgressEvent Progress(
        Guid runId, string phase = "Executing", int completed = 1, int total = 10,
        bool paused = false, long scannedSources = 0) =>
        new()
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            Phase = phase,
            Completed = completed,
            Total = total,
            Deleted = 0,
            Paused = paused,
            ScannedSources = scannedSources,
        };

    private static RunPlannedEvent Planned(Guid runId, string name = "Photos", string? error = null) =>
        new()
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = Guid.NewGuid(),
            ProfileName = name,
            PlannedCopies = 4,
            PlannedDeletes = 0,
            PlannedCopyBytes = 2048,
            PlannedDeleteBytes = 0,
            Truncated = false,
            Error = error,
        };

    private static RunCompletedEvent Completed(Guid runId, string outcome = "Succeeded") =>
        new()
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = Guid.NewGuid(),
            Outcome = outcome,
            Succeeded = 4,
            Skipped = 0,
            Failed = 0,
            Deleted = 0,
            BytesDeleted = 0,
        };

    [Fact]
    public async Task Reconcile_seeds_the_queue_from_the_service()
    {
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(Guid.NewGuid()), Summary(Guid.NewGuid()) },
        };
        JobQueueViewModel queue = new(gateway);

        await queue.ReconcileAsync();

        Assert.Equal(2, queue.Runs.Count);
        Assert.True(queue.HasRuns);
        Assert.Equal(2, queue.ActiveCount);
    }

    /// <summary>Rows are MUTATED across a reconcile rather than replaced, so selection survives. A replaced
    /// instance would deselect the row the user is looking at on every reconnect.</summary>
    [Fact]
    public async Task Reconcile_keeps_the_selected_row_and_updates_it_in_place()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId, succeeded: 3) },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();
        JobQueueRow row = queue.Runs[0];
        queue.SelectedRun = row;

        gateway.RunsResult = new List<RunSummaryDto> { Summary(runId, succeeded: 7) };
        await queue.ReconcileAsync();

        Assert.Same(row, queue.SelectedRun);
        Assert.Same(row, queue.Runs[0]);
        Assert.Equal(7, row.Completed);
    }

    /// <summary>A transient outage must not blank the queue — the rows on screen are still the best
    /// picture available.</summary>
    [Fact]
    public async Task A_failed_reconcile_leaves_the_existing_rows_alone()
    {
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(Guid.NewGuid()) },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();

        gateway.RunsResult = new IpcError("SERVICE_UNAVAILABLE", "not running");
        await queue.ReconcileAsync();

        Assert.Single(queue.Runs);
        Assert.Contains("Could not load the run queue", queue.ErrorMessage);
    }

    [Fact]
    public void A_planned_run_becomes_a_row_waiting_for_approval()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunPlanned(Planned(runId));

        JobQueueRow row = Assert.Single(queue.Runs);
        Assert.True(row.IsAwaitingApproval);
        Assert.Equal("Photos", row.ProfileName);
        Assert.Equal(4, row.PlannedCopies);
        // A parked run is doing nothing, so there is nothing to hold.
        Assert.False(row.IsPausable);
        Assert.True(row.IsCancellable);
    }

    /// <summary>A plan that FAILED is already closed engine-side, so its row must not sit offering to
    /// approve something that no longer exists.</summary>
    [Fact]
    public void A_failed_plan_lands_as_a_closed_row_not_an_approvable_one()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunPlanned(Planned(runId, error: "the source folder is not available"));

        JobQueueRow row = Assert.Single(queue.Runs);
        Assert.False(row.IsAwaitingApproval);
        Assert.True(row.IsFinished);
        Assert.True(row.HasProblem);
        Assert.Equal(0, queue.ActiveCount);
    }

    /// <summary>Unlike the per-file activity feed, a run gets a synthesized row from a progress sample
    /// alone: a queue silently missing an executing run is far worse than one row with a blank name.</summary>
    [Fact]
    public void Progress_for_an_unseen_run_still_produces_a_row()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());

        queue.OnRunProgress(Progress(Guid.NewGuid(), completed: 5, total: 20));

        JobQueueRow row = Assert.Single(queue.Runs);
        Assert.Equal(5, row.Completed);
        Assert.Equal(0.25, row.ProgressFraction);
        Assert.Contains("5 of 20", row.ProgressText);
    }

    /// <summary>run-completed is authoritative. A late progress sample — the stream is lossy AND unordered
    /// under back-pressure — must not resurrect a finished run as running.</summary>
    [Fact]
    public void A_late_progress_sample_cannot_resurrect_a_finished_run()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        queue.OnRunCompleted(Completed(runId));

        queue.OnRunProgress(Progress(runId, completed: 9));

        JobQueueRow row = Assert.Single(queue.Runs);
        Assert.True(row.IsFinished);
        Assert.Equal("Finished", row.StatusText);
        Assert.Equal(0, queue.ActiveCount);
    }

    /// <summary>A run cancelled while paused must not keep reading "Paused" forever.</summary>
    [Fact]
    public void Finishing_clears_the_paused_flag()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId, paused: true));
        Assert.True(queue.Runs[0].Paused);

        queue.OnRunCompleted(Completed(runId, "Cancelled"));

        Assert.False(queue.Runs[0].Paused);
        Assert.Equal("Cancelled", queue.Runs[0].StatusText);
    }

    /// <summary>Paused wins over the phase in the caption: it is what the user did, and the reason the
    /// counters have stopped moving. Without it a paused run and a wedged one read identically.</summary>
    [Fact]
    public void A_paused_run_says_so_rather_than_looking_wedged()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunProgress(Progress(runId, paused: true));

        JobQueueRow row = queue.Runs[0];
        Assert.Equal("Paused", row.StatusText);
        Assert.Equal("Resume", row.PauseLabel);
        Assert.Equal("IconPlay", row.PauseIconKey);
    }

    /// <summary>"Waiting to start" and "scanning, nothing found yet" mean very different things and must
    /// not read the same — the distinction the Waiting phase exists for.</summary>
    [Fact]
    public void A_run_queued_behind_the_plan_limit_says_it_is_waiting()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());

        queue.OnRunProgress(Progress(Guid.NewGuid(), phase: "Waiting", completed: 0, total: 0));

        JobQueueRow row = queue.Runs[0];
        Assert.Equal("Waiting to start", row.StatusText);
        Assert.Equal("Queued behind other previews", row.ProgressText);
    }

    [Fact]
    public void A_planning_run_reports_its_scan_counts_since_it_has_no_denominator()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());

        queue.OnRunProgress(Progress(
            Guid.NewGuid(), phase: "Planning", completed: 0, total: 0, scannedSources: 12_345));

        Assert.Contains("12,345 source file(s)", queue.Runs[0].ProgressText);
        Assert.True(queue.Runs[0].IsIndeterminate);
    }

    /// <summary>The invariant the two-phase run exists to protect: nobody approves work they have not
    /// looked at. A run this window did not plan gets "View plan", not Approve.</summary>
    [Fact]
    public void Approve_is_offered_only_for_a_plan_this_window_showed()
    {
        Guid mine = Guid.NewGuid(), theirs = Guid.NewGuid();
        JobQueueViewModel queue = new(new FakeIpcGateway()) { IsOwnRun = id => id == mine };

        queue.OnRunPlanned(Planned(mine));
        queue.OnRunPlanned(Planned(theirs));

        Assert.True(queue.Runs.Single(r => r.RunId == mine).CanApproveHere);
        Assert.False(queue.Runs.Single(r => r.RunId == theirs).CanApproveHere);
        // Both are still discardable and cancellable — declining work you did not start is always safe.
        Assert.True(queue.Runs.Single(r => r.RunId == theirs).IsAwaitingApproval);
        Assert.True(queue.Runs.Single(r => r.RunId == theirs).IsCancellable);
    }

    [Fact]
    public async Task Pausing_a_row_reaches_the_service_and_flips_optimistically()
    {
        FakeIpcGateway gateway = new();
        JobQueueViewModel queue = new(gateway);
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        JobQueueRow row = queue.Runs[0];

        await queue.TogglePauseCommand.ExecuteAsync(row);

        Assert.Equal((runId, true), Assert.Single(gateway.SetRunPausedCalls));
        Assert.True(row.Paused);
    }

    /// <summary>A refused pause must put the toggle back, or the row lies about a state the engine never
    /// entered.</summary>
    [Fact]
    public async Task A_refused_pause_puts_the_toggle_back()
    {
        FakeIpcGateway gateway = new()
        {
            SetRunPausedResult = new IpcError("IPC_TRANSPORT", "the pipe is gone"),
        };
        JobQueueViewModel queue = new(gateway);
        queue.OnRunProgress(Progress(Guid.NewGuid()));
        JobQueueRow row = queue.Runs[0];

        await queue.TogglePauseCommand.ExecuteAsync(row);

        Assert.False(row.Paused);
        Assert.Contains("Could not pause the run", queue.ErrorMessage);
    }

    /// <summary>RUN_NOT_FOUND means the run finished or was pruned while the click was in flight — a normal
    /// race for a view fed by a lossy stream, so it re-seeds instead of raising a banner.</summary>
    [Fact]
    public async Task Pausing_a_run_that_has_just_ended_reconciles_instead_of_complaining()
    {
        FakeIpcGateway gateway = new()
        {
            SetRunPausedResult = new IpcError("RUN_NOT_FOUND", "no run with that id"),
        };
        JobQueueViewModel queue = new(gateway);
        queue.OnRunProgress(Progress(Guid.NewGuid()));

        await queue.TogglePauseCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Null(queue.ErrorMessage);
        Assert.Equal(1, gateway.GetRunsCalls);
    }

    [Fact]
    public async Task Cancelling_a_row_reaches_the_service()
    {
        FakeIpcGateway gateway = new();
        JobQueueViewModel queue = new(gateway);
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));

        await queue.CancelRunCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Equal(runId, Assert.Single(gateway.CancelRunCalls));
    }

    [Fact]
    public async Task Approve_routes_through_the_shell()
    {
        List<(Guid RunId, bool Approve)> answered = [];
        Guid mine = Guid.NewGuid();
        JobQueueViewModel queue = new(new FakeIpcGateway())
        {
            IsOwnRun = _ => true,
            AnswerRun = (runId, approve) =>
            {
                answered.Add((runId, approve));
                return Task.CompletedTask;
            },
        };
        queue.OnRunPlanned(Planned(mine));

        await queue.ApproveCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Equal([(mine, true)], answered);
    }

    /// <summary>Discard does NOT route through the shell's answer path any more. Declining a plan merely
    /// closes it, and a closed run is now retained — so a discarded preview would linger in the queue as a
    /// row for a plan the user said no to. Discard removes it outright.</summary>
    [Fact]
    public async Task Discarding_a_parked_plan_removes_the_row_rather_than_declining_it()
    {
        FakeIpcGateway gateway = new();
        List<(Guid RunId, bool Approve)> answered = [];
        Guid mine = Guid.NewGuid();
        JobQueueViewModel queue = new(gateway)
        {
            IsOwnRun = _ => true,
            AnswerRun = (runId, approve) => { answered.Add((runId, approve)); return Task.CompletedTask; },
        };
        queue.OnRunPlanned(Planned(mine));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Equal(mine, Assert.Single(gateway.DiscardRunCalls));
        Assert.Empty(answered);
        Assert.Empty(queue.Runs);
    }

    /// <summary>The row goes on discard, and the shell is told so anything else holding the run (the
    /// retained-preview store) can let go.</summary>
    [Fact]
    public async Task Discarding_a_finished_run_removes_the_row_and_notifies_the_shell()
    {
        FakeIpcGateway gateway = new();
        List<Guid> discarded = [];
        JobQueueViewModel queue = new(gateway) { DiscardedRun = discarded.Add };
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        queue.OnRunCompleted(Completed(runId));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Equal(runId, Assert.Single(gateway.DiscardRunCalls));
        Assert.Equal(runId, Assert.Single(discarded));
        Assert.Empty(queue.Runs);
        Assert.False(queue.HasRuns);
    }

    /// <summary>A run still going is confirmed first — discarding it cancels real work. Declining the
    /// prompt must leave the run entirely alone.</summary>
    [Fact]
    public async Task Discarding_a_live_run_confirms_first_and_a_refusal_changes_nothing()
    {
        FakeIpcGateway gateway = new();
        List<string> prompts = [];
        JobQueueViewModel queue = new(gateway)
        {
            ConfirmDiscard = message => { prompts.Add(message); return Task.FromResult(false); },
        };
        queue.OnRunProgress(Progress(Guid.NewGuid()));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Single(prompts);
        // The copy has to say what discarding a live run actually does, since it is not obvious.
        Assert.Contains("cancelled", prompts[0]);
        Assert.Empty(gateway.DiscardRunCalls);
        Assert.Single(queue.Runs);
    }

    /// <summary>A FINISHED run is not confirmed: there is no work to lose, and a prompt on every tidy-up
    /// would train the user to click through the one that matters.</summary>
    [Fact]
    public async Task Discarding_a_finished_run_does_not_confirm()
    {
        FakeIpcGateway gateway = new();
        List<string> prompts = [];
        JobQueueViewModel queue = new(gateway)
        {
            ConfirmDiscard = message => { prompts.Add(message); return Task.FromResult(false); },
        };
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        queue.OnRunCompleted(Completed(runId));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Empty(prompts);
        Assert.Single(gateway.DiscardRunCalls);
    }

    /// <summary>REGRESSION. A discarded run publishes its OWN closing events, and every handler here
    /// synthesizes a row for a run it does not know — so cancelling a planning run made <c>PlanAsync</c>
    /// publish run-planned (carrying the cancellation as its error) and run-completed, and both arrived at a
    /// queue that had just dropped the row and dutifully put it back. What the user saw was Discard
    /// cancelling the job and leaving the entry sitting there.</summary>
    [Fact]
    public async Task A_discarded_runs_own_closing_events_cannot_resurrect_its_row()
    {
        FakeIpcGateway gateway = new();
        JobQueueViewModel queue = new(gateway);
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId, phase: "Planning", completed: 0, total: 0));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);
        Assert.Empty(queue.Runs);

        // Exactly what the engine publishes as a cancelled plan unwinds, in order.
        queue.OnRunPlanned(Planned(runId, error: "the run was cancelled while planning"));
        queue.OnRunCompleted(Completed(runId, "Cancelled"));
        queue.OnRunProgress(Progress(runId));

        Assert.Empty(queue.Runs);
        Assert.False(queue.HasRuns);
        Assert.Equal(0, queue.ActiveCount);
    }

    /// <summary>The same resurrection by the other route: a <c>get-runs</c> already in flight when Discard
    /// was pressed can still name the run.</summary>
    [Fact]
    public async Task A_reconcile_cannot_bring_back_a_discarded_run()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId) },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);
        await queue.ReconcileAsync();   // the service has not caught up, or the answer was already in flight

        Assert.Empty(queue.Runs);
    }

    /// <summary>A discard that genuinely FAILED leaves the run there, so the row must go back to updating
    /// rather than sitting frozen at whatever it last said.</summary>
    [Fact]
    public async Task A_failed_discard_keeps_the_row_and_lets_it_keep_updating()
    {
        FakeIpcGateway gateway = new()
        {
            DiscardRunResult = new IpcError("IPC_TRANSPORT", "the pipe is gone"),
        };
        JobQueueViewModel queue = new(gateway);
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId, completed: 1, total: 10));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Single(queue.Runs);
        Assert.Contains("Could not discard the run", queue.ErrorMessage);

        queue.OnRunProgress(Progress(runId, completed: 7, total: 10));
        Assert.Equal(7, queue.Runs[0].Completed);
    }

    /// <summary>RUN_NOT_FOUND means it was already gone, which is the outcome the user asked for — so the
    /// row still goes and no banner appears.</summary>
    [Fact]
    public async Task Discarding_a_run_that_is_already_gone_still_removes_the_row()
    {
        FakeIpcGateway gateway = new()
        {
            DiscardRunResult = new IpcError("RUN_NOT_FOUND", "no run with that id"),
        };
        JobQueueViewModel queue = new(gateway);
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        queue.OnRunCompleted(Completed(runId));

        await queue.DiscardCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Empty(queue.Runs);
        Assert.Null(queue.ErrorMessage);
    }

    /// <summary>Approve must not be reachable for a run whose plan was never shown, even if something
    /// invokes the command directly.</summary>
    [Fact]
    public async Task Approving_a_run_this_window_never_planned_does_nothing()
    {
        List<Guid> answered = [];
        JobQueueViewModel queue = new(new FakeIpcGateway())
        {
            IsOwnRun = _ => false,
            AnswerRun = (runId, _) => { answered.Add(runId); return Task.CompletedTask; },
        };
        queue.OnRunPlanned(Planned(Guid.NewGuid()));

        await queue.ApproveCommand.ExecuteAsync(queue.Runs[0]);

        Assert.Empty(answered);
    }

    // ── Finished runs are kept, and age ──────────────────────────────────────────────────────────
    // The behaviour this whole change is about: a finished run used to be FORGOTTEN by the engine ten
    // minutes after it ended. Now that mark dims the row instead.

    /// <summary>A run that has just finished is not old, and one past the threshold is — driven by the
    /// injected clock rather than by waiting out ten real minutes.</summary>
    [Fact]
    public void A_finished_run_reads_as_old_only_once_it_is_past_the_threshold()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        JobQueueViewModel queue = new(new FakeIpcGateway(), clock);
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        queue.OnRunCompleted(Completed(runId) with { AtUtc = clock.GetUtcNow() });
        JobQueueRow row = queue.Runs[0];

        Assert.False(row.IsOld);
        Assert.True(row.HasFinishedAge);
        Assert.Equal("finished just now", row.FinishedAgeText);

        clock.Advance(TimeSpan.FromMinutes(9));
        queue.RefreshAges();
        Assert.False(row.IsOld);
        Assert.Equal("finished 9 min ago", row.FinishedAgeText);

        clock.Advance(TimeSpan.FromMinutes(2));
        queue.RefreshAges();
        Assert.True(row.IsOld);
        Assert.Equal("finished 11 min ago", row.FinishedAgeText);

        clock.Advance(TimeSpan.FromHours(3));
        queue.RefreshAges();
        Assert.Contains("3 hr ago", row.FinishedAgeText);
    }

    /// <summary>A LIVE run is never old and has no finished age, however long it has been going.</summary>
    [Fact]
    public void A_running_run_is_never_old()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        JobQueueViewModel queue = new(new FakeIpcGateway(), clock);
        queue.OnRunProgress(Progress(Guid.NewGuid()));

        clock.Advance(TimeSpan.FromDays(2));
        queue.RefreshAges();

        Assert.False(queue.Runs[0].IsOld);
        Assert.False(queue.Runs[0].HasFinishedAge);
        Assert.Equal("", queue.Runs[0].FinishedAgeText);
    }

    /// <summary>A reconcile carries the close time too, so a row the window is seeing for the FIRST time —
    /// it was not listening when the run ended — still ages correctly instead of looking brand new.</summary>
    [Fact]
    public async Task A_reconciled_row_ages_from_when_the_run_actually_closed()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto>
            {
                Summary(Guid.NewGuid(), "Closed", "Succeeded",
                    closedAt: clock.GetUtcNow() - TimeSpan.FromHours(5)),
            },
        };
        JobQueueViewModel queue = new(gateway, clock);

        await queue.ReconcileAsync();

        Assert.True(queue.Runs[0].IsOld);
        Assert.Equal("finished 5 hr ago", queue.Runs[0].FinishedAgeText);
    }

    /// <summary>The trim drops FINISHED rows first, so a long session never evicts a live run in favour of
    /// a closed one it happens to be newer than.</summary>
    [Fact]
    public void Trimming_evicts_finished_rows_before_live_ones()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid live = Guid.NewGuid();
        queue.OnRunProgress(Progress(live));                       // oldest, and still running
        for (int i = 0; i < JobQueueViewModel.MaxRows; i++)
        {
            Guid runId = Guid.NewGuid();
            queue.OnRunProgress(Progress(runId));
            queue.OnRunCompleted(Completed(runId));
        }

        Assert.Equal(JobQueueViewModel.MaxRows, queue.Runs.Count);
        Assert.Contains(queue.Runs, r => r.RunId == live);
        Assert.Equal(1, queue.ActiveCount);
    }
}
