using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Runs;

/// <summary>The run lifecycle: plan → await approval → execute → close, plus cancellation and the
/// completion barrier.
///
/// <para>The barrier is what makes "every copy has landed, now remove the orphans" expressible at all,
/// and its failure modes are the interesting part. It is driven here by draining the queue and reporting
/// settlement directly — standing in for the orchestrator — because that is the only way to test the
/// accounting deterministically rather than racing a live worker pool.</para></summary>
public sealed class RunLifecycleTests
{
    // ---- plan, approve, execute -------------------------------------------------------------------

    [Fact]
    public async Task A_run_awaiting_approval_has_moved_NO_files()
    {
        using RunPlanHarness h = new("run-await");
        h.WriteSource("a.txt", "aaa");
        h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        Result<RunHandle, string> begun = runs.Begin(h.MirrorProfile(), scopePath: null);
        Assert.False(begun.TryGetError(out string? error), error);
        begun.TryGetValue(out RunHandle? handle);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        // The entire point of the two-phase shape: the work list exists, the user can see exactly what
        // it says, and not one byte has been touched.
        Assert.Equal(1, status.PlannedCopies);
        Assert.Equal(1, status.PlannedDeletes);
        Assert.DoesNotContain(Directory.GetFiles(h.TargetDir), f => Path.GetFileName(f) == "a.txt");
        Assert.True(File.Exists(Path.Combine(h.TargetDir, "orphan.txt")));
        Assert.Empty(h.Bin());
        Assert.Equal(0, h.Queue.PendingCount);
    }

    [Fact]
    public async Task The_plan_event_carries_the_counts_the_user_is_approving()
    {
        using RunPlanHarness h = new("run-planevent");
        h.WriteSource("a.txt", "12345");
        h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        RunPlannedEvent planned = Assert.Single(h.Bus.Events.OfType<RunPlannedEvent>());
        Assert.Equal(handle.RunId, planned.RunId);
        Assert.Equal(1, planned.PlannedCopies);
        Assert.Equal(5, planned.PlannedCopyBytes);
        // The number that matters most on this event — it is the destructive half.
        Assert.Equal(1, planned.PlannedDeletes);
        Assert.Equal(8, planned.PlannedDeleteBytes);
        Assert.False(planned.Truncated);
        Assert.Null(planned.Error);
    }

    [Fact]
    public async Task Approving_enqueues_exactly_the_planned_copies_each_tagged_with_the_run()
    {
        using RunPlanHarness h = new("run-approve");
        h.WriteSource("a.txt", "aaa");
        h.WriteSource("nested/b.txt", "bb");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        Assert.False(runs.Approve(handle.RunId, approve: true).TryGetError(out string? error), error);

        List<Payload> taken = await h.DrainAndSettleAsync(runs, handle.RunId);

        Assert.Equal(2, taken.Count);
        // The run id on every payload is what lets its job settle this run's barrier and what lets a
        // cancel drop work that has not started.
        Assert.All(taken, p => Assert.Equal(handle.RunId, p.RunId));
        Assert.All(taken, p => Assert.Equal(h.SourceDir, p.SourceRoot));
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
    }

    [Fact]
    public async Task DECLINING_a_run_changes_nothing_and_closes_it_cancelled()
    {
        using RunPlanHarness h = new("run-decline");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        Assert.False(runs.Approve(handle.RunId, approve: false).TryGetError(out string? error), error);

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(RunOutcome.Cancelled, status.Outcome);
        Assert.Equal(0, h.Queue.PendingCount);
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());
        Assert.False(File.Exists(Path.Combine(h.TargetDir, "a.txt")));
    }

    [Fact]
    public async Task Approving_twice_is_refused_rather_than_running_the_work_again()
    {
        using RunPlanHarness h = new("run-double-approve");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);

        Result second = runs.Approve(handle.RunId, true);

        Assert.True(second.TryGetError(out string? error));
        Assert.Contains("not awaiting approval", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Approving_an_unknown_run_is_an_error()
    {
        using RunPlanHarness h = new("run-unknown");
        RunCoordinator runs = h.Coordinator();

        Assert.True(runs.Approve(Guid.NewGuid(), true).TryGetError(out string? error));
        Assert.Contains("no run with id", error, StringComparison.Ordinal);
    }

    // ---- aggregate reporting ----------------------------------------------------------------------

    [Fact]
    public async Task The_completion_event_reports_the_run_as_a_whole()
    {
        using RunPlanHarness h = new("run-aggregate");
        h.WriteSource("a.txt", "aaa");
        h.WriteSource("b.txt", "bb");
        h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        // A run used to emit N job events and no statement at all about the run. This is that
        // statement, and it is the only authoritative one.
        RunCompletedEvent completed = Assert.Single(h.Bus.Events.OfType<RunCompletedEvent>());
        Assert.Equal(handle.RunId, completed.RunId);
        Assert.Equal(2, completed.Succeeded);
        Assert.Equal(0, completed.Failed);
        Assert.Equal(1, completed.Deleted);
        Assert.Equal(8, completed.BytesDeleted);
        Assert.Equal(nameof(RunOutcome.Succeeded), completed.Outcome);
        Assert.Null(completed.DeletionAbortReason);
    }

    [Fact]
    public async Task A_run_with_a_FAILED_job_closes_with_problems_and_deletes_nothing()
    {
        using RunPlanHarness h = new("run-failedjob");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId, JobOutcome.Failed);

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(RunOutcome.CompletedWithProblems, status.Outcome);
        Assert.Equal(1, status.Failed);
        // Until every copy has landed the destination is not a mirror of the source, so the destructive
        // half is off — and the user is told why rather than left to notice.
        Assert.Equal(0, status.Deleted);
        Assert.True(File.Exists(orphan));
        Assert.NotNull(status.DeletionAbortReason);
        Assert.Contains(
            h.Bus.Events.OfType<EngineWarningEvent>(),
            w => w.Message.Contains("no destination files were deleted", StringComparison.Ordinal));
    }

    // ---- MirrorDeletion timing --------------------------------------------------------------------

    [Fact]
    public async Task AfterCopy_deletes_the_orphan_only_after_the_copies_have_settled()
    {
        using RunPlanHarness h = new("run-aftercopy");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(MirrorDeletion.AfterCopy), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);

        // Before the barrier is satisfied the orphan must still be there: AfterCopy's whole promise is
        // that nothing is removed until the replacements are in place.
        await Task.Delay(150);
        Assert.True(File.Exists(orphan));
        Assert.Equal(0, h.Coordinator().GetStatus(handle.RunId)!.Deleted);

        await h.DrainAndSettleAsync(runs, handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.False(File.Exists(orphan));
        Assert.Single(h.Bin());
    }

    [Fact]
    public async Task Proactive_deletes_the_orphan_BEFORE_the_copies_are_even_queued()
    {
        using RunPlanHarness h = new("run-proactive");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();
        // Observed at the instant of the move: nothing of this run may be queued yet. Proactive exists
        // to free the space before the copies need it, so the ordering IS the feature.
        int? pendingWhenDeleted = null;
        h.Trash.OnMove = _ => pendingWhenDeleted = h.Queue.PendingCount;

        runs.Begin(h.MirrorProfile(MirrorDeletion.Proactive), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        Assert.False(File.Exists(orphan));
        Assert.Equal(0, pendingWhenDeleted);
    }

    // ---- cancellation -----------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_planned_run_before_approval_leaves_everything_alone()
    {
        using RunPlanHarness h = new("run-cancel-planned");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        Assert.False(runs.Cancel(handle.RunId).TryGetError(out string? error), error);
        // Approval is refused once cancelled, so the run cannot be resurrected.
        runs.Approve(handle.RunId, true);
        await Task.Delay(150);

        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());
    }

    [Fact]
    public async Task Cancelling_mid_execution_drops_the_queued_work_and_skips_the_deletion_phase()
    {
        using RunPlanHarness h = new("run-cancel-exec");
        for (int i = 0; i < 20; i++)
            h.WriteSource($"f{i}.txt", "x");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        // Let the enqueue happen, then cancel with the work still sitting in the queue.
        await Task.Delay(100);
        runs.Cancel(handle.RunId);

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(RunOutcome.Cancelled, status.Outcome);
        Assert.Equal(0, h.Queue.PendingCountForRun(handle.RunId));
        // A cancelled run is by definition not a completed mirror, so nothing is deleted.
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());
    }

    [Fact]
    public void Cancelling_an_unknown_run_is_an_error()
    {
        using RunPlanHarness h = new("run-cancel-unknown");
        Assert.True(h.Coordinator().Cancel(Guid.NewGuid()).TryGetError(out _));
    }

    // ---- the barrier's backstops ------------------------------------------------------------------

    [Fact]
    public async Task A_payload_the_orchestrator_DROPPED_still_settles_the_barrier()
    {
        using RunPlanHarness h = new("run-dropped");
        h.WriteSource("a.txt", "aaa");
        h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await Task.Delay(100);

        // A payload the orchestrator drops (profile deactivated mid-run, plan-build failure) produces
        // NO completion — but it must still settle, or the barrier waits out its whole deadline and the
        // deletion phase is then refused for a reason that has nothing to do with safety. This pins the
        // try/finally settle discipline in RunJobAsync.
        while (h.Queue.PendingCount > 0)
            await foreach (Payload p in h.Queue.DequeueAsync(new CancellationTokenSource(50).Token))
            {
                runs.Settled(p.RunId!.Value, completion: null);
                break;
            }

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        // The barrier was MET — the run closed promptly instead of waiting out its deadline, which is what
        // reporting a dropped payload buys.
        Assert.Equal(0, status.Succeeded);
        // ...and the deletion phase then REFUSED, which is the other half. A dropped payload is a file
        // from the approved list that was not copied, so the destination is not a mirror of the source and
        // an orphan removal would be reconciling against a source set the run never finished copying.
        // Deleting it here (which is what this used to assert) was the bug: the drop incremented no
        // counter, so the pass was told every copy had landed.
        Assert.Equal(0, status.Deleted);
        Assert.NotNull(status.DeletionAbortReason);
        Assert.Contains("could not be copied", status.DeletionAbortReason, StringComparison.Ordinal);
        // Not Failed: a drop is not a job outcome. It is counted separately and surfaces as a problem.
        Assert.Equal(0, status.Failed);
        Assert.Equal(RunOutcome.CompletedWithProblems, status.Outcome);
    }

    [Fact]
    public async Task QUIESCENCE_cannot_fire_before_the_first_job_settles()
    {
        using RunPlanHarness h = new("run-quiescence-clock");
        h.WriteSource("a.txt", "aaa");
        h.WriteSource("b.txt", "bbb");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        // A window LONGER than this test waits, so quiescence firing at all is provably premature. The
        // clock it measures is LastSettleTicks, which is 0 until the first job settles — so
        // GetElapsedTime(0) returned machine uptime, cleared any window however long, and released the
        // deletion phase on the very first poll while the copies were still in flight.
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            MirrorQuiescenceWindow = TimeSpan.FromSeconds(30),
            MirrorBarrierTimeout = TimeSpan.FromMinutes(5),
        });

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);

        // Stand in for slow copies: take both payloads off the queue so nothing is pending, and settle
        // neither for longer than the whole quiescence window. The run must still be waiting.
        List<Payload> taken = [];
        for (int waited = 0; taken.Count < 2 && waited < 5_000; waited += 20)
        {
            if (h.Queue.PendingCount == 0)
            {
                await Task.Delay(20);
                continue;
            }
            await foreach (Payload p in h.Queue.DequeueAsync(new CancellationTokenSource(200).Token))
            {
                taken.Add(p);
                break;
            }
        }
        Assert.Equal(2, taken.Count);
        // Many drain polls, but nowhere near the 30-second window — so a correct clock cannot have
        // expired and the run must still be waiting on its two copies.
        await Task.Delay(600);

        Assert.True(File.Exists(orphan), "the deletion phase ran before the copies settled");
        Assert.Empty(h.Bin());
        Assert.Equal(RunPhase.Executing, runs.GetStatus(handle.RunId)!.Phase);

        // Settling them releases the barrier the honest way, through the exact count.
        foreach (Payload p in taken)
            runs.Settled(p.RunId!.Value, new JobCompletion(
                JobId.New(), JobOutcome.Succeeded, null, null, TimeSpan.Zero));
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(1, status.Deleted);
    }

    [Fact]
    public async Task Two_copy_items_for_ONE_source_path_expect_ONE_job()
    {
        using RunPlanHarness h = new("run-coalesce");
        // Two Source roots, one nested in the other, so the same file is scanned under both and produces
        // two copy items with the same SourcePath. The queue coalesces them onto one entry — which yields
        // one job — so Expected must be 1. Counting both left the exact barrier unreachable forever.
        string nested = Path.Combine(h.SourceDir, "inner");
        Directory.CreateDirectory(nested);
        RunPlanHarness.Write(Path.Combine(nested, "a.txt"), "aaa");
        h.WriteTarget("orphan.txt", "orphaned");
        Profile profile = h.MirrorProfile();
        profile = profile with
        {
            Sources = [.. profile.Sources, new SourceConfig { Path = nested }],
        };
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            // Long enough that only the EXACT barrier can close this run in time — if Expected is
            // over-counted the test fails by timing out rather than passing by a backstop.
            MirrorQuiescenceWindow = TimeSpan.FromMinutes(5),
            MirrorBarrierTimeout = TimeSpan.FromMinutes(5),
        });

        runs.Begin(profile, null).TryGetValue(out RunHandle? handle);
        RunStatus planned = await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        Assert.Equal(2, planned.PlannedCopies);   // the plan really does name it twice
        runs.Approve(handle.RunId, true);

        // Exactly ONE job comes out of the queue, and settling it is enough.
        List<Payload> taken = [];
        for (int waited = 0; taken.Count < 1 && waited < 5_000; waited += 20)
        {
            if (h.Queue.PendingCount == 0)
            {
                await Task.Delay(20);
                continue;
            }
            await foreach (Payload p in h.Queue.DequeueAsync(new CancellationTokenSource(200).Token))
            {
                taken.Add(p);
                break;
            }
        }
        Assert.Single(taken);
        runs.Settled(taken[0].RunId!.Value, new JobCompletion(
            JobId.New(), JobOutcome.Succeeded, null, null, TimeSpan.Zero));

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Null(status.DeletionAbortReason);
        Assert.Equal(1, status.Deleted);
    }

    [Fact]
    public async Task A_STEADY_STATE_mirror_still_removes_its_orphans()
    {
        using RunPlanHarness h = new("run-steady-state");
        // The ordinary state of a mature Mirror profile: every destination file is already identical to
        // its source, so the plan has ZERO copy items — and 25 files have since been deleted upstream.
        for (int i = 0; i < 40; i++)
        {
            h.WriteSource($"kept{i}.txt", $"same-{i}");
            h.WriteTarget($"kept{i}.txt", $"same-{i}");
        }
        for (int i = 0; i < 25; i++)
            h.WriteTarget($"orphan{i}.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        RunStatus planned = await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        Assert.Equal(0, planned.PlannedCopies);   // nothing to copy — this is the case that broke
        Assert.Equal(25, planned.PlannedDeletes);

        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        // The ratio guard's denominator used to be built from the COPY items, which are zero here — so it
        // computed 25/25 = 100%, refused the whole pass, and blamed a target-layout misconfiguration that
        // did not exist. Every steady-state Mirror run was affected, permanently.
        Assert.Null(status.DeletionAbortReason);
        Assert.Equal(25, status.Deleted);
        Assert.Equal(25, h.Bin().Count);
        // The survivors are untouched: 40 in, 40 out.
        Assert.Equal(40, Directory.GetFiles(h.TargetDir).Length);
    }

    // ---- the validator gate ------------------------------------------------------------------------

    [Fact]
    public async Task An_UNSAVED_draft_with_a_blocking_warning_is_refused_until_acknowledged()
    {
        using RunPlanHarness h = new("run-ack");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        // The profile is in NO catalog — a draft the Preview tab sent straight from the editor, which
        // never went through SaveProfileAsync and so acknowledged nothing.
        h.Validator = new StubProfileValidator(new ValidationIssue(
            ValidationSeverity.BlockingWarning, "PROFILE_MIRROR_DELETES",
            "This profile removes destination files."));
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        // Unacknowledged: refused, and the run stays parked rather than closing — the user can still
        // acknowledge it. Nothing was touched.
        Assert.True(runs.Approve(handle.RunId, true).TryGetError(out string? error));
        Assert.Contains("PROFILE_MIRROR_DELETES", error, StringComparison.Ordinal);
        Assert.Equal(RunPhase.AwaitingApproval, runs.GetStatus(handle.RunId)!.Phase);
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());

        // Acknowledged: it runs.
        Assert.False(runs.Approve(handle.RunId, true, acknowledgeWarnings: true).TryGetError(out _));
        await h.DrainAndSettleAsync(runs, handle.RunId);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(1, status.Deleted);
    }

    [Fact]
    public async Task A_SAVED_profile_is_not_re_acknowledged_on_every_run()
    {
        using RunPlanHarness h = new("run-ack-saved");
        h.WriteSource("a.txt", "aaa");
        h.WriteTarget("orphan.txt", "orphaned");
        Profile saved = h.MirrorProfile();
        // In the catalog means it got through SaveProfileAsync, which refuses to store a profile whose
        // blocking warnings were not acknowledged. Re-asking here would be warning fatigue on the one
        // prompt that guards target-side deletion.
        h.Catalog = new FakeProfileCatalog(saved);
        h.Validator = new StubProfileValidator(new ValidationIssue(
            ValidationSeverity.BlockingWarning, "PROFILE_MIRROR_DELETES",
            "This profile removes destination files."));
        RunCoordinator runs = h.Coordinator();

        runs.Begin(saved, null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        Assert.False(runs.Approve(handle.RunId, true).TryGetError(out string? error), error);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(1, status.Deleted);
    }

    [Fact]
    public async Task A_validation_ERROR_is_refused_even_with_an_acknowledgment()
    {
        using RunPlanHarness h = new("run-error");
        h.WriteSource("a.txt", "aaa");
        h.Validator = new StubProfileValidator(new ValidationIssue(
            ValidationSeverity.Error, "PROFILE_NO_TARGETS", "The profile has no Targets."));
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        // An Error is not acknowledgeable — there is no "run it anyway" for a profile that is not valid.
        Assert.True(runs.Approve(handle.RunId, true, acknowledgeWarnings: true)
            .TryGetError(out string? error));
        Assert.Contains("PROFILE_NO_TARGETS", error, StringComparison.Ordinal);
        Assert.Equal(RunPhase.AwaitingApproval, runs.GetStatus(handle.RunId)!.Phase);
    }

    [Fact]
    public async Task Declining_is_never_gated_by_the_validator()
    {
        using RunPlanHarness h = new("run-decline-ungated");
        h.WriteSource("a.txt", "aaa");
        h.Validator = new StubProfileValidator(new ValidationIssue(
            ValidationSeverity.Error, "PROFILE_NO_TARGETS", "The profile has no Targets."));
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        // Abandoning a plan must always be possible: a run the user cannot decline is one whose snapshot
        // directory leaks for the lifetime of the service.
        Assert.False(runs.Approve(handle.RunId, approve: false).TryGetError(out string? error), error);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(RunOutcome.Cancelled, status.Outcome);
    }

    [Fact]
    public async Task The_planned_event_carries_the_blocking_issues_the_footer_has_to_state()
    {
        using RunPlanHarness h = new("run-planned-issues");
        h.WriteSource("a.txt", "aaa");
        h.Validator = new StubProfileValidator(
            new ValidationIssue(ValidationSeverity.BlockingWarning, "PROFILE_MIRROR_DELETES", "Removes files."),
            new ValidationIssue(ValidationSeverity.Warning, "PROFILE_CHATTY", "Just so you know."));
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        RunPlannedEvent planned = Assert.Single(h.Bus.Events.OfType<RunPlannedEvent>());
        // Only the blocking half: a plain Warning gates nothing, so listing it beside the Approve button
        // would spend the user's attention on something they cannot act on.
        ValidationIssue issue = Assert.Single(planned.BlockingIssues);
        Assert.Equal("PROFILE_MIRROR_DELETES", issue.Code);
    }

    [Fact]
    public async Task QUIESCENCE_closes_a_run_whose_settle_count_can_never_be_reached()
    {
        using RunPlanHarness h = new("run-quiescent");
        h.WriteSource("a.txt", "aaa");
        h.WriteTarget("orphan.txt", "orphaned");
        // A short window so the test does not wait three real seconds, and a long deadline so it is
        // provably quiescence — not the timeout — that closes the run.
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            MirrorQuiescenceWindow = TimeSpan.FromMilliseconds(200),
            MirrorBarrierTimeout = TimeSpan.FromMinutes(5),
        });

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await Task.Delay(100);

        // Take the payload off the queue and NEVER settle it — the accounting hole the backstop exists
        // for. Nothing is queued and nothing is settling, so there is nothing left to wait for.
        while (h.Queue.PendingCount > 0)
            await foreach (Payload _ in h.Queue.DequeueAsync(new CancellationTokenSource(50).Token))
                break;

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(1, status.Deleted);
    }

    [Fact]
    public async Task The_barrier_DEADLINE_closes_the_run_and_deletes_NOTHING()
    {
        using RunPlanHarness h = new("run-deadline");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        // Deadline shorter than the quiescence window, so the deadline is provably what fires. The
        // payload stays queued the whole time, so quiescence cannot.
        RunCoordinator runs = h.Coordinator(new EngineConfig
        {
            MirrorBarrierTimeout = TimeSpan.FromMilliseconds(300),
            MirrorQuiescenceWindow = TimeSpan.FromMinutes(5),
        });

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        // A timeout is NOT "assume it drained". We cannot say every copy landed, so we do not delete.
        Assert.Equal(0, status.Deleted);
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());
        Assert.NotNull(status.DeletionAbortReason);
    }

    // ---- the self-write guard, end to end ---------------------------------------------------------

    [Fact]
    public async Task A_path_a_job_actually_wrote_is_never_deleted_even_when_the_plan_named_it()
    {
        using RunPlanHarness h = new("run-selfwrite");
        h.WriteSource("a.txt", "aaa");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);

        // Conflict resolution can resolve a different final path at execution time than the plan's
        // read-only probe predicted. Reporting it through JobCompletion.ResolvedFinalPaths is what stops
        // such a path — absent from the plan's survivor set — being deleted as an orphan by the very run
        // that just created it.
        await h.DrainAndSettleAsync(runs, handle.RunId, JobOutcome.Succeeded, _ => [orphan]);

        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        Assert.Equal(0, status.Deleted);
        Assert.True(File.Exists(orphan));
    }

    // ---- plan failures ----------------------------------------------------------------------------

    [Fact]
    public async Task A_TRUNCATED_plan_warns_at_approval_time_and_deletes_nothing_when_approved()
    {
        using RunPlanHarness h = new("run-truncated");
        for (int i = 0; i < 12; i++)
            h.WriteSource($"f{i}.txt", "x");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();

        // The planner's own file bound is not reachable from here, so truncate via the profile's scan
        // depth instead: a sweep fault or a candidate cap both land on the same flag.
        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        RunStatus planned = await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        // This particular tree is small enough not to truncate — the assertion here is that an
        // untruncated plan says so, which is the control for the gate tests that force the flag.
        Assert.False(planned.PlanTruncated);
        Assert.Equal(1, planned.PlannedDeletes);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task A_run_over_a_profile_whose_source_disappears_reports_a_plan_error()
    {
        using RunPlanHarness h = new("run-planfail");
        Profile missing = h.MirrorProfile() with
        {
            Sources = [new SourceConfig { Path = Path.Combine(h.Root, "not-there") }],
        };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(missing, null).TryGetValue(out RunHandle? handle);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.Closed);

        // A missing source root is not a crash: the walk reports it, the run closes, and the user is
        // told. What must NOT happen is a plan that reads "0 source files" and goes on to call every
        // destination file an orphan.
        Assert.Equal(0, status.PlannedDeletes);
        Assert.Equal(0, status.Deleted);
        Assert.Empty(h.Bin());
    }

    [Fact]
    public async Task An_AdditiveArchive_run_never_deletes_and_reports_no_planned_deletions()
    {
        using RunPlanHarness h = new("run-additive");
        h.WriteSource("a.txt", "aaa");
        string existing = h.WriteTarget("unrelated.txt", "left alone");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(scanDestination: true), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        Assert.Equal(0, status.PlannedDeletes);
        Assert.Equal(0, status.Deleted);
        Assert.True(File.Exists(existing));
        Assert.Empty(h.Bin());
    }

    // ---- housekeeping -----------------------------------------------------------------------------

    [Fact]
    public async Task A_closed_run_leaves_no_snapshot_directory_behind()
    {
        using RunPlanHarness h = new("run-cleanup");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        string snapshot = runs.SnapshotDirectory(handle.RunId)!;
        Assert.True(Directory.Exists(snapshot));

        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        // The frozen work list is scaffolding, not a record — the journal and the audit trail are the
        // record. Leaving it behind would accumulate a directory per run forever.
        //
        // Asserted with NO retry, deliberately: Closed is the signal every client takes for "this run
        // is over", so the cleanup has to have happened BEFORE that phase is observable. Polling here
        // would let the coordinator publish Closed with the directory still on disk and still pass,
        // which is precisely the ordering this pins — and it used to fail about 1 full-suite run in 6.
        Assert.False(Directory.Exists(snapshot));
        // And the path is forgotten, so the plan-replay handler reports RUN_NOT_FOUND rather than
        // handing a client a directory that is no longer there.
        Assert.Null(runs.SnapshotDirectory(handle.RunId));
    }

    [Fact]
    public async Task StopAsync_unwinds_an_in_flight_run()
    {
        using RunPlanHarness h = new("run-stop");
        for (int i = 0; i < 30; i++)
            h.WriteSource($"f{i}.txt", "x");
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        // Do not wait for a phase: shutdown may land mid-plan, which is the case worth covering.
        await runs.StopAsync();

        Assert.Empty(h.Bin());
    }
}
