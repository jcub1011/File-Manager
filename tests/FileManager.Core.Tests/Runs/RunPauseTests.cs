using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using FileManager.Core.Runs;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Runs;

/// <summary>Per-run pause, and concurrent runs — the two things the job queue is built on.
///
/// <para>The rule under test throughout: <b>a per-run pause withholds work that has not started and never
/// aborts anything.</b> That is what separates it from the global engine pause, which the Mirror deletion
/// pass treats as a reason to abort fail-closed. A user who wants a run to stop has cancel.</para></summary>
public sealed class RunPauseTests
{
    // ---- concurrent runs --------------------------------------------------------------------------

    /// <summary>Several runs planning and parked at once. Structurally the coordinator always supported
    /// this — a ConcurrentDictionary, a CTS and a task per run — but nothing exercised it, and retained
    /// previews make it the ordinary state rather than a curiosity.</summary>
    [Fact]
    public async Task Several_runs_can_be_planned_and_parked_at_the_same_time()
    {
        using RunPlanHarness h = new("run-concurrent");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();

        List<Guid> ids = [];
        for (int i = 0; i < 3; i++)
        {
            runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
            ids.Add(handle!.RunId);
        }
        foreach (Guid id in ids)
            await RunPlanHarness.WaitForPhaseAsync(runs, id, RunPhase.AwaitingApproval);

        // All three are simultaneously parked, each with its own plan and its own snapshot.
        foreach (Guid id in ids)
        {
            Assert.Equal(RunPhase.AwaitingApproval, runs.GetStatus(id)!.Phase);
            Assert.NotNull(runs.SnapshotDirectory(id));
        }
        Assert.Equal(3, runs.ListRuns().Count);
        Assert.Equal(3, ids.Distinct().Count());
    }

    /// <summary>A parked run must NOT hold a planning slot. With previews retained, several parked runs are
    /// normal — and if a slot were held for each, the queue would deadlock after MaxConcurrentPlans
    /// previews and no further preview would ever start.</summary>
    [Fact]
    public async Task A_parked_run_holds_no_planning_slot()
    {
        using RunPlanHarness h = new("run-slots");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator(new EngineConfig { MaxConcurrentPlans = 1 });

        // Four consecutive previews against a limit of one. Each must complete: if the slot were released
        // at CLOSE rather than at the end of the walk, the second would wait forever.
        for (int i = 0; i < 4; i++)
        {
            runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
            await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        }

        Assert.Equal(4, runs.ListRuns().Count(r => r.Phase == nameof(RunPhase.AwaitingApproval)));
    }

    // ---- get-runs ---------------------------------------------------------------------------------

    /// <summary>The queue's re-seed. Note ProfileName: it comes from the FROZEN profile because a run
    /// planned from an unsaved draft is in no catalog, so a client-side lookup would leave exactly the
    /// runs the GUI starts permanently nameless.</summary>
    [Fact]
    public async Task ListRuns_describes_a_parked_run_including_its_profile_name()
    {
        using RunPlanHarness h = new("run-list");
        h.WriteSource("a.txt", "12345");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        RunSummaryDto summary = Assert.Single(runs.ListRuns());
        Assert.Equal(handle.RunId, summary.RunId);
        Assert.Equal(nameof(RunPhase.AwaitingApproval), summary.Phase);
        Assert.False(string.IsNullOrEmpty(summary.ProfileName));
        Assert.Equal(1, summary.PlannedCopies);
        Assert.False(summary.Paused);
        Assert.False(summary.Waiting);
        // Set once the work list froze — what a client measures a stored preview's staleness against.
        Assert.NotNull(summary.PlannedAtUtc);
    }

    [Fact]
    public void ListRuns_on_an_idle_engine_is_empty_not_an_error()
    {
        using RunPlanHarness h = new("run-list-empty");
        Assert.Empty(h.Coordinator().ListRuns());
    }

    // ---- pausing --------------------------------------------------------------------------------

    [Fact]
    public async Task Pausing_and_resuming_a_run_is_reported_and_idempotent()
    {
        using RunPlanHarness h = new("run-pause-state");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        Assert.False(runs.SetPaused(handle.RunId, true).TryGetError(out _));
        Assert.True(h.RunPause.IsRunPaused(handle.RunId));
        Assert.True(Assert.Single(runs.ListRuns()).Paused);

        // Idempotent: pausing an already-paused run is not an error, so a double-click on a toggle does
        // not produce a banner.
        Assert.False(runs.SetPaused(handle.RunId, true).TryGetError(out _));

        Assert.False(runs.SetPaused(handle.RunId, false).TryGetError(out _));
        Assert.False(h.RunPause.IsRunPaused(handle.RunId));
    }

    [Fact]
    public void Pausing_an_unknown_run_is_an_error()
    {
        using RunPlanHarness h = new("run-pause-unknown");
        Assert.True(h.Coordinator().SetPaused(Guid.NewGuid(), true).TryGetError(out _));
    }

    /// <summary>A closed run cannot be paused, and saying so beats accepting it silently — a queue row
    /// showing "Paused" for a run that has already finished is a lie the user would act on.</summary>
    [Fact]
    public async Task Pausing_a_finished_run_is_refused()
    {
        using RunPlanHarness h = new("run-pause-closed");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, approve: false);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        Assert.True(runs.SetPaused(handle.RunId, true).TryGetError(out string? error));
        Assert.Contains("already finished", error);
    }

    /// <summary>A closed run's flag is FORGOTTEN, so a run cancelled while paused does not leave an entry
    /// behind — and, since Forget does not notify, produces no phantom resume telling the queue to look for
    /// work that no longer exists.</summary>
    [Fact]
    public async Task Closing_a_paused_run_forgets_its_pause_flag()
    {
        using RunPlanHarness h = new("run-pause-forget");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.SetPaused(handle.RunId, true);

        runs.Cancel(handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        Assert.False(h.RunPause.IsRunPaused(handle.RunId));
        Assert.False(Assert.Single(runs.ListRuns()).Paused);
    }

    /// <summary>Pausing an EXECUTING run withholds the copies that have not started. The queue's own tests
    /// cover the dequeue filter; this pins the run-level consequence — the work stays pending rather than
    /// being dropped, so resuming really does run it.</summary>
    [Fact]
    public async Task Pausing_an_executing_run_holds_its_queued_copies_without_dropping_them()
    {
        using RunPlanHarness h = new("run-pause-exec");
        for (int i = 0; i < 8; i++)
            h.WriteSource($"f{i}.txt", "x");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        runs.SetPaused(handle.RunId, true);
        runs.Approve(handle.RunId, true);
        await Task.Delay(200);   // let the enqueue finish

        Assert.Equal(8, h.Queue.PendingCountForRun(handle.RunId));
        // Nothing is servable while it is held — the consumer would walk straight past every entry.
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));
        await using IAsyncEnumerator<Payload> e =
            h.Queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> move = e.MoveNextAsync();
        await Task.Delay(150);
        Assert.False(move.IsCompleted, "a paused run's copies must not be served");

        runs.SetPaused(handle.RunId, false);
        Assert.True(await move);
        Assert.Equal(handle.RunId, e.Current.RunId);
    }

    // ---- the deletion pass: wait vs abort ---------------------------------------------------------

    /// <summary>The two pauses have deliberately different semantics inside the deletion pass, and this is
    /// the pair that states it.
    ///
    /// <para>The GLOBAL pause is a safety brake on the whole engine, so a destructive pass under it aborts
    /// fail-closed. A PER-RUN pause is the user saying "hold this one" about a run they already approved;
    /// answering that by abandoning its deletion half would leave the destination not a mirror of the
    /// source, reported as an abort reason they never asked for. So it WAITS — and, because waiting
    /// between orphans holds no lock and no journal handle, costs nothing but time.</para></summary>
    [Fact]
    public async Task A_per_run_pause_makes_the_deletion_pass_WAIT_where_a_global_pause_aborts()
    {
        using RunPlanHarness h = new("pause-pass-wait");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        Guid runId = Guid.NewGuid();

        h.RunPause.Set(runId, true);
        Task<MirrorDeletionResult> pass = h.Pass.DeleteAsync(h.Request(dir, runId: runId));
        await Task.Delay(300);

        // Held, not refused: nothing has been removed and the pass has not answered.
        Assert.False(pass.IsCompleted, "a per-run pause must hold the pass, not end it");
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());

        h.RunPause.Set(runId, false);
        MirrorDeletionResult result = await pass;

        // And on resume it finishes the job it was holding — the orphan really goes.
        Assert.Equal(1, result.Deleted);
        Assert.Null(result.AbortReason);
        Assert.False(File.Exists(orphan));
    }

    /// <summary>The global pause keeps its existing fail-closed abort. Pinned beside the test above so the
    /// asymmetry reads as a decision rather than an oversight.</summary>
    [Fact]
    public async Task A_global_pause_still_aborts_the_deletion_pass()
    {
        using RunPlanHarness h = new("pause-pass-abort");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        h.Pause.SetPaused(true);

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        Assert.Equal(0, result.Deleted);
        Assert.Contains("paused", result.AbortReason!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());
    }

    // ---- the barrier clock ------------------------------------------------------------------------

    /// <summary>THE test this feature needed. The drain barrier's deadline is MirrorBarrierTimeout, and
    /// expiring it sets BarrierTimedOut, which refuses the Mirror deletion phase. Without subtracting the
    /// paused interval, pausing a run over a lunch break would bring it back and delete nothing —
    /// reported as unaccounted copies, which is a safety message about something that was never unsafe.
    ///
    /// <para>Driven with a FakeTimeProvider so the pause is exact: the coordinator's poll interval is real
    /// (200 ms of Task.Delay), while every elapsed-time decision it makes reads this clock.</para></summary>
    [Fact]
    public async Task A_long_pause_does_not_burn_the_drain_barriers_deadline()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("run-pause-barrier") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        // A deliberately tiny deadline, so "paused for far longer than the timeout" is a fast test.
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            MirrorBarrierTimeout = TimeSpan.FromSeconds(30),
            MirrorQuiescenceWindow = TimeSpan.FromSeconds(3),
        });

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.SetPaused(handle.RunId, true);
        runs.Approve(handle.RunId, true);
        await Task.Delay(200);   // the enqueue happens; the drain loop starts polling

        // Ten minutes of pause — twenty times the deadline. The clock moves; the deadline must not.
        clock.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(200);
        runs.SetPaused(handle.RunId, false);

        // Now let the work settle as the orchestrator would.
        await h.DrainAndSettleAsync(runs, handle.RunId);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        // The barrier was MET, not timed out — so the deletion phase ran and the orphan really went.
        Assert.Equal(1, status.Succeeded);
        Assert.Equal(1, status.Deleted);
        Assert.Null(status.DeletionAbortReason);
        Assert.False(File.Exists(orphan));
    }

    /// <summary>The quiescence backstop must not fire for a paused run either. Its ordinary guard is
    /// "nothing of ours is pending", which a paused run satisfies — but not in the window after its last
    /// payload was dequeued, where nothing is pending, the job is finishing, and the settle clock has run
    /// past the window. Reading that as drained would start the deletion phase while the user believes the
    /// run is held.</summary>
    [Fact]
    public async Task A_paused_run_is_never_treated_as_drained_by_the_quiescence_backstop()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("run-pause-quiesce") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            MirrorBarrierTimeout = TimeSpan.FromMinutes(30),
            MirrorQuiescenceWindow = TimeSpan.FromSeconds(3),
        });

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await Task.Delay(200);

        // Take the payload OUT of the queue but never settle it, then pause: nothing pending, nothing
        // settling — the exact shape quiescence looks for.
        h.Queue.DropRun(handle.RunId);
        runs.SetPaused(handle.RunId, true);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(400);   // several poll intervals

        // Still executing: the pause held the backstop off, so the destructive phase never started.
        Assert.Equal(RunPhase.Executing, runs.GetStatus(handle.RunId)!.Phase);
        Assert.True(File.Exists(orphan));

        runs.Cancel(handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
    }

    /// <summary>Cancel supersedes pause, and this is not housekeeping.
    ///
    /// <para>The test above establishes that a paused run is never treated as drained — which is right,
    /// or the destructive phase could start behind the user's back. But that same guard means a run
    /// cancelled WHILE PAUSED has nothing left that can release its drain loop: its work is dropped, so
    /// nothing will ever settle, and quiescence is held off by the pause. It sat in Executing until the
    /// 30-minute barrier deadline expired, never reaching Closed, never cleaning up its snapshot
    /// directory, and never leaving the job queue.</para>
    ///
    /// <para>Found by the teardown of the previous test hanging. Pinned here so the fix (Cancel clears the
    /// pause flag) reads as the decision it is.</para></summary>
    [Fact]
    public async Task Cancelling_a_paused_run_closes_it_instead_of_waiting_out_the_barrier()
    {
        using RunPlanHarness h = new("run-cancel-paused");
        for (int i = 0; i < 4; i++)
            h.WriteSource($"f{i}.txt", "x");
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            // If the pause were left set, the run could only close by burning this — so a deadline far
            // longer than the test's own timeout is what makes the assertion meaningful.
            MirrorBarrierTimeout = TimeSpan.FromMinutes(30),
        });

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.SetPaused(handle.RunId, true);
        runs.Approve(handle.RunId, true);
        await Task.Delay(200);   // the copies are queued, and held

        runs.Cancel(handle.RunId);

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(RunOutcome.Cancelled, status.Outcome);
        Assert.False(h.RunPause.IsRunPaused(handle.RunId));
        Assert.Equal(0, h.Queue.PendingCountForRun(handle.RunId));
    }
}
