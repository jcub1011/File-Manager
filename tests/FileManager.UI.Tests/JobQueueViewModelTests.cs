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
        bool paused = false, long scannedSources = 0, string name = "Photos") =>
        new()
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = Guid.NewGuid(),
            ProfileName = name,
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

    // ── The reconcile diffs rather than rebuilds ────────────────────────────────────────────────
    // An open queue window re-reads the list every couple of seconds. The old reconcile cleared the
    // collection and refilled it, which was fine on window-open and on reconnect and is not fine twice a
    // second: it resets the scroll position and makes a virtualized list rebuild every realized row.

    /// <summary>The common case, and the one that matters for an open window: nothing changed, so the
    /// collection must raise NO events at all.</summary>
    [Fact]
    public async Task An_unchanged_reconcile_does_not_touch_the_collection()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(a), Summary(b) },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();

        int changes = 0;
        queue.Runs.CollectionChanged += (_, _) => changes++;
        await queue.ReconcileAsync();
        await queue.ReconcileAsync();

        Assert.Equal(0, changes);
        Assert.Equal(2, queue.Runs.Count);
    }

    /// <summary>A new run appears without the rest of the list being rebuilt around it — one insert, and the
    /// rows already there keep their instances.</summary>
    [Fact]
    public async Task A_new_run_is_inserted_without_disturbing_the_existing_rows()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        FakeIpcGateway gateway = new() { RunsResult = new List<RunSummaryDto> { Summary(a) } };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();
        JobQueueRow existing = queue.Runs[0];

        // get-runs is newest-first, so the new run leads.
        gateway.RunsResult = new List<RunSummaryDto> { Summary(b), Summary(a) };
        await queue.ReconcileAsync();

        Assert.Equal(2, queue.Runs.Count);
        Assert.Equal(b, queue.Runs[0].RunId);
        Assert.Same(existing, queue.Runs[1]);   // the row that was already there was not rebuilt
    }

    /// <summary>A run the response no longer names has gone from the engine, so its row goes too.</summary>
    [Fact]
    public async Task A_run_the_response_omits_is_removed()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(a), Summary(b) },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();

        gateway.RunsResult = new List<RunSummaryDto> { Summary(a) };
        await queue.ReconcileAsync();

        Assert.Equal(a, Assert.Single(queue.Runs).RunId);
    }

    /// <summary>Order follows the response (newest first), so a run that moved position is moved rather
    /// than rebuilt — and the selection, which is an instance, survives it.</summary>
    [Fact]
    public async Task A_reordered_response_moves_rows_and_keeps_the_selection()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(a), Summary(b), Summary(c) },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();
        JobQueueRow selected = queue.Runs[2];
        queue.SelectedRun = selected;

        gateway.RunsResult = new List<RunSummaryDto> { Summary(c), Summary(a), Summary(b) };
        await queue.ReconcileAsync();

        Assert.Equal([c, a, b], queue.Runs.Select(r => r.RunId));
        Assert.Same(selected, queue.SelectedRun);
        Assert.Same(selected, queue.Runs[0]);
    }

    /// <summary>The watch loop is what makes an open window keep up when the event stream drops a frame —
    /// for this view a dropped announcement means the run is simply absent.</summary>
    [Fact]
    public async Task The_watch_loop_reconciles_until_it_is_cancelled()
    {
        FakeIpcGateway gateway = new();
        JobQueueViewModel queue = new(gateway);
        using CancellationTokenSource cts = new();

        Task watching = queue.WatchAsync(cts.Token);
        // POLLED for the first tick rather than sleeping three intervals and hoping. Sleeping a fixed span
        // and then asserting a poll HAS happened is the racy direction, and at three 2-second intervals it
        // also spent six seconds of suite time to learn something true after two.
        await WaitUntilAsync(() => gateway.GetRunsCalls >= 1);
        int polled = gateway.GetRunsCalls;

        await cts.CancelAsync();
        await watching;   // must return rather than throw

        // The one wait that must stay a sleep: this asserts polling has STOPPED, and there is no state
        // transition to poll for — only the absence of one. Load only makes that direction safer, so a
        // single interval is enough to catch a loop that ignored its token.
        await Task.Delay(JobQueueViewModel.WatchInterval);

        Assert.Equal(polled, gateway.GetRunsCalls);   // and stop polling once cancelled
    }

    /// <summary>The watch loop is fire-and-forget, so tests poll for its effects rather than await them —
    /// the same convention <c>ActivityViewModelTests</c> follows.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
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
    /// alone: a queue silently missing an executing run is far worse than one extra row.</summary>
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

    /// <summary>A run is ANNOUNCED by its first progress sample, before it has a plan — so that sample has
    /// to carry the profile, or the row it creates is nameless for the whole of planning (minutes, on a
    /// slow source). A client cannot look the name up: a draft-planned run is in no catalog.</summary>
    [Fact]
    public void The_row_a_progress_sample_creates_knows_its_profile()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());

        queue.OnRunProgress(Progress(
            Guid.NewGuid(), phase: "Planning", completed: 0, total: 0, name: "Backup"));

        JobQueueRow row = Assert.Single(queue.Runs);
        Assert.Equal("Backup", row.ProfileName);
        Assert.NotEqual(Guid.Empty, row.ProfileId);
    }

    /// <summary>A sample from an older service carries no name, and must not blank one a run-planned already
    /// supplied.</summary>
    [Fact]
    public void A_nameless_progress_sample_does_not_blank_a_name_already_known()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();
        queue.OnRunPlanned(Planned(runId, name: "Backup"));

        queue.OnRunProgress(Progress(runId, name: ""));

        Assert.Equal("Backup", queue.Runs[0].ProfileName);
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
        Assert.True(queue.Runs[0].ShowProgress);
    }

    /// <summary>A run parked for approval shows NO bar.
    ///
    /// <para>It used to show one on the grounds that it was not finished, which implies progress that is not
    /// happening — nothing runs behind those buttons until the user presses Approve. Worse, a parked plan
    /// with nothing to copy has no denominator either, so the bar sat spinning indefinitely on a run that
    /// was waiting on the user rather than working.</para></summary>
    [Theory]
    [InlineData(4)]    // a parked plan with work to do
    [InlineData(0)]    // and one with none, which is where the spinner used to appear
    public void A_run_parked_for_approval_shows_no_progress_bar(int plannedCopies)
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunPlanned(Planned(runId) with { PlannedCopies = plannedCopies });

        JobQueueRow row = queue.Runs[0];
        Assert.True(row.IsAwaitingApproval);
        Assert.False(row.ShowProgress);
        Assert.False(row.IsIndeterminate);
    }

    /// <summary>And a finished run shows none either — it is a record, not work.</summary>
    [Fact]
    public void A_finished_run_shows_no_progress_bar()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();
        queue.OnRunProgress(Progress(runId));
        Assert.True(queue.Runs[0].ShowProgress);   // executing

        queue.OnRunCompleted(Completed(runId));

        Assert.False(queue.Runs[0].ShowProgress);
        Assert.False(queue.Runs[0].IsIndeterminate);
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

    // ── Byte progress ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar measures DATA once the wire supplies a byte total, which is the whole point of the
    /// byte pipeline: a run of one disk image and ten thousand thumbnails sits at 99% by file count while
    /// the image is still copying.</summary>
    [Fact]
    public void An_executing_row_measures_bytes_when_the_wire_reports_them()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunProgress(Progress(runId, completed: 9_999, total: 10_000) with
        {
            CompletedBytes = 250,
            TotalBytes = 1000,
        });

        JobQueueRow row = queue.Runs[0];
        Assert.True(row.HasByteProgress);
        Assert.Equal(0.25, row.ProgressFraction, 3);          // bytes, NOT 9999/10000
        Assert.Equal("250 B of 1000 B", row.ByteProgressText);
    }

    /// <summary>Against a service too old to report bytes, both figures arrive as zero — and the row must
    /// fall back to counting files rather than showing a bar pinned at 0% while the file count visibly
    /// advances. That silent-wrong-answer is the one failure a mixed-version pair could otherwise
    /// produce.</summary>
    [Fact]
    public void An_executing_row_falls_back_to_files_when_no_byte_total_is_reported()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunProgress(Progress(runId, completed: 5, total: 10));

        JobQueueRow row = queue.Runs[0];
        Assert.False(row.HasByteProgress);
        Assert.Equal(0.5, row.ProgressFraction, 3);
    }

    /// <summary>Clamped, because the two ends come from different measurements: the numerator is summed
    /// from per-job sizes recorded at plan time, and a re-screened job can settle against a file that grew.</summary>
    [Fact]
    public void Byte_progress_clamps_at_one()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunProgress(Progress(runId) with { CompletedBytes = 5000, TotalBytes = 1000 });

        Assert.Equal(1.0, queue.Runs[0].ByteProgressFraction, 3);
    }

    /// <summary>The re-seed carries the byte pair too. Without it, reopening the queue part-way through a
    /// run would show the bar back at zero, which reads as the run having restarted.</summary>
    [Fact]
    public async Task A_reconcile_reseeds_the_byte_figures()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId) with { BytesSettled = 512 } },
        };
        JobQueueViewModel queue = new(gateway);

        await queue.ReconcileAsync();

        JobQueueRow row = queue.Runs[0];
        Assert.Equal(512, row.CompletedBytes);
        Assert.Equal(1024, row.TotalBytes);      // Summary's PlannedCopyBytes
        Assert.Equal(0.5, row.ByteProgressFraction, 3);
    }

    /// <summary>The planning phase's two counts, split so the destination line can DISAPPEAR rather than
    /// read zero — most profiles never sweep their destination, and "0 at the destination" reads as a
    /// fault.</summary>
    [Fact]
    public void A_planning_row_reports_its_scan_counts_on_separate_lines()
    {
        JobQueueViewModel queue = new(new FakeIpcGateway());
        Guid runId = Guid.NewGuid();

        queue.OnRunProgress(Progress(runId, phase: "Planning", completed: 0, total: 0, scannedSources: 1234));

        JobQueueRow row = queue.Runs[0];
        Assert.True(row.IsPlanning);
        Assert.Equal("Scanned 1,234 source file(s)", row.ScanSourcesText);
        Assert.False(row.HasScannedDestinations);

        queue.OnRunProgress(Progress(runId, phase: "Planning", completed: 0, total: 0, scannedSources: 1234)
            with { ScannedDestinations = 7 });

        Assert.True(row.HasScannedDestinations);
        Assert.Equal("Scanned 7 at the destination", row.ScanDestinationsText);
    }

    // ── The summary pane ────────────────────────────────────────────────────────────────────────────

    private static RunDetailDto Detail(Guid runId, string? scope = null) => new(
        runId, TestData.ProfileFactory.Sample(), scope, DateTimeOffset.UnixEpoch,
        CopyItemCount: 4, CopyBytes: 2048, DeleteItemCount: 0, DeleteBytes: 0,
        SourceItemCount: 9, DestinationItemCount: 4,
        OverwriteCount: 2, RenameCount: 0, DisposalCount: 9,
        Truncated: false, SweepFaultDetail: null, Space: null);

    [Fact]
    public async Task Selecting_a_run_loads_its_summary()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId, phase: "AwaitingApproval") },
        };
        gateway.RunDetailResults[runId] = Detail(runId);
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();

        queue.SelectedRun = queue.Runs[0];
        await Settle();

        Assert.Equal([runId], gateway.GetRunDetailCalls);
        Assert.NotNull(queue.Summary.Plan);
        Assert.False(queue.Summary.IsLoading);
        // The sample profile's own shape: one source root, one target root.
        Assert.Equal([@"C:\ui-test\src"], queue.Summary.Plan!.Sources.Select(r => r.Path));
        Assert.Equal([@"C:\ui-test\dst"], queue.Summary.Plan.Targets.Select(r => r.Path));
    }

    /// <summary>Deselecting must blank the pane rather than leave the last run's summary under no name.</summary>
    [Fact]
    public async Task Deselecting_clears_the_summary()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId, phase: "AwaitingApproval") },
        };
        gateway.RunDetailResults[runId] = Detail(runId);
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();
        queue.SelectedRun = queue.Runs[0];
        await Settle();
        Assert.NotNull(queue.Summary.Plan);

        queue.SelectedRun = null;

        Assert.Null(queue.Summary.Plan);
        Assert.False(queue.Summary.HasNoPlanYet);
        Assert.Null(queue.Summary.ErrorText);
    }

    /// <summary>RUN_PLAN_UNAVAILABLE is the NORMAL answer for a run still planning — its snapshot header is
    /// written when planning finishes — so it must reach the pane as "no plan yet" and never as an error.</summary>
    [Fact]
    public async Task A_run_still_planning_reports_no_plan_yet_rather_than_an_error()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId, phase: "Planning") },
        };
        JobQueueViewModel queue = new(gateway);   // gateway defaults to RUN_PLAN_UNAVAILABLE
        await queue.ReconcileAsync();

        queue.SelectedRun = queue.Runs[0];
        await Settle();

        Assert.True(queue.Summary.HasNoPlanYet);
        Assert.Null(queue.Summary.ErrorText);
        Assert.Null(queue.Summary.Plan);
    }

    /// <summary>run-planned is the moment a plan becomes readable, so the pane re-asks — otherwise a user
    /// watching a scan finish would sit on "still working out what this will do" until they clicked away and
    /// back.</summary>
    [Fact]
    public async Task The_summary_is_re_fetched_when_the_selected_runs_plan_arrives()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId, phase: "Planning") },
        };
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();
        queue.SelectedRun = queue.Runs[0];
        await Settle();
        Assert.True(queue.Summary.HasNoPlanYet);
        Assert.Single(gateway.GetRunDetailCalls);

        // The plan lands; now there IS a header to read.
        gateway.RunDetailResults[runId] = Detail(runId);
        queue.OnRunPlanned(Planned(runId));
        await Settle();

        Assert.Equal(2, gateway.GetRunDetailCalls.Count);
        Assert.NotNull(queue.Summary.Plan);
    }

    /// <summary>The two-second watch loop re-resolves the selection on every poll. Refetching there would
    /// issue a request twice a second for a selection nobody has touched.</summary>
    [Fact]
    public async Task A_reconcile_that_re_resolves_the_same_selection_does_not_refetch()
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto> { Summary(runId, phase: "AwaitingApproval") },
        };
        gateway.RunDetailResults[runId] = Detail(runId);
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();
        queue.SelectedRun = queue.Runs[0];
        await Settle();
        Assert.Single(gateway.GetRunDetailCalls);

        await queue.ReconcileAsync();
        await queue.ReconcileAsync();
        await Settle();

        Assert.Same(queue.Runs[0], queue.SelectedRun);
        Assert.Single(gateway.GetRunDetailCalls);
    }

    /// <summary>A superseded fetch must not land. Walking the list with the arrow keys starts one per row,
    /// and without cancellation a slow answer for an earlier row could arrive last and leave the pane
    /// describing a run that is no longer selected.</summary>
    [Fact]
    public async Task A_superseded_summary_fetch_never_reaches_the_pane()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RunsResult = new List<RunSummaryDto>
            {
                Summary(first, phase: "AwaitingApproval"),
                Summary(second, phase: "AwaitingApproval"),
            },
        };
        // The first run's fetch is held open; the second answers at once. The two answers differ by SCOPE,
        // which is what lets the assertions below tell which one reached the pane.
        TaskCompletionSource held = new();
        gateway.RunDetailGates[first] = held;
        gateway.RunDetailResults[first] = Detail(first, scope: @"C:\only-a-subfolder");
        gateway.RunDetailResults[second] = Detail(second);
        JobQueueViewModel queue = new(gateway);
        await queue.ReconcileAsync();

        queue.SelectedRun = queue.Runs.First(r => r.RunId == first);
        await Settle();
        Assert.True(queue.Summary.IsLoading);

        queue.SelectedRun = queue.Runs.First(r => r.RunId == second);
        await Settle();
        Assert.NotNull(queue.Summary.Plan);
        Assert.False(queue.Summary.Plan!.HasScope);

        // Release the stale fetch. It is both cancelled and no longer the selected run, so it must be
        // dropped on the floor rather than overwrite the pane with the scoped answer.
        held.SetResult();
        await Settle();

        Assert.NotNull(queue.Summary.Plan);
        Assert.False(queue.Summary.Plan!.HasScope);
    }

    /// <summary>Lets a fire-and-forget load run to completion. The fake answers synchronously, so the
    /// continuations are already queued; the loop is a bound, not a delay.</summary>
    private static async Task Settle()
    {
        for (int i = 0; i < 50; i++)
            await Task.Yield();
    }
}
