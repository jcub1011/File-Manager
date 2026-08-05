using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Runs;

/// <summary>How long a run lives, and what ends it.
///
/// <para>A finished run used to be FORGOTTEN ten minutes after it closed, on a hard-coded timer. That was
/// written when a run was invisible plumbing whose only consumer was "a client that asks just after the
/// terminal event still gets an answer". Now a run is a row the user looks at, so it is kept until the user
/// discards it — or until auto-delete reaps it, which is configurable and can be switched off.</para>
///
/// <para>Nothing tested retention before this file: <c>ClosedRunRetention</c> and <c>PruneClosedRuns</c>
/// had zero test hits, so none of these rules had any coverage at all.</para></summary>
public sealed class RunRetentionTests
{
    /// <summary>Plans a run and answers it, leaving a CLOSED run in the coordinator. Declining rather than
    /// executing, because what these tests care about is the closed state, not how it got there.</summary>
    private static async Task<Guid> ClosedRunAsync(RunPlanHarness h, RunCoordinator runs)
    {
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, approve: false);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);
        return handle.RunId;
    }

    private static GlobalSettings Retention(bool autoDelete, int hours = 24) =>
        new() { AutoDeleteFinishedRuns = autoDelete, FinishedRunRetentionHours = hours };

    // ---- the regression this change is about -------------------------------------------------------

    /// <summary>THE point of the whole change. A finished run survives well past the ten-minute mark that
    /// used to delete it — the mark now only means the UI dims the row.</summary>
    [Fact]
    public async Task A_finished_run_is_still_listed_long_after_the_old_ten_minute_window()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-past-ten") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        Guid runId = await ClosedRunAsync(h, runs);

        clock.Advance(TimeSpan.FromHours(6));
        runs.Begin(h.AdditiveProfile(), null);   // Begin sweeps; the closed run must survive it

        Assert.Contains(runs.ListRuns(), r => r.RunId == runId);
        Assert.NotNull(runs.GetStatus(runId));
    }

    // ---- auto-delete --------------------------------------------------------------------------------

    /// <summary>With auto-delete on, a run goes once it is past the configured interval — and NOT before.
    /// The negative half matters as much as the positive: an off-by-one here silently deletes history.</summary>
    [Fact]
    public async Task Auto_delete_removes_a_finished_run_only_once_it_is_past_the_interval()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-auto") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        h.SettingsProvider.Current = Retention(autoDelete: true, hours: 2);
        RunCoordinator runs = h.Coordinator();
        Guid runId = await ClosedRunAsync(h, runs);

        clock.Advance(TimeSpan.FromMinutes(119));
        runs.Begin(h.AdditiveProfile(), null);
        Assert.Contains(runs.ListRuns(), r => r.RunId == runId);

        clock.Advance(TimeSpan.FromMinutes(2));
        runs.Begin(h.AdditiveProfile(), null);
        Assert.DoesNotContain(runs.ListRuns(), r => r.RunId == runId);
    }

    /// <summary>With auto-delete OFF, a finished run survives an arbitrarily long wait. This is the setting
    /// the user asked for, and the reason the count backstop below has to exist.</summary>
    [Fact]
    public async Task Auto_delete_off_keeps_a_finished_run_indefinitely()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-off") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        h.SettingsProvider.Current = Retention(autoDelete: false);
        RunCoordinator runs = h.Coordinator();
        Guid runId = await ClosedRunAsync(h, runs);

        clock.Advance(TimeSpan.FromDays(400));
        runs.Begin(h.AdditiveProfile(), null);

        Assert.Contains(runs.ListRuns(), r => r.RunId == runId);
    }

    /// <summary>The settings are read on every sweep, not captured at construction — so turning auto-delete
    /// on takes effect without restarting the service.</summary>
    [Fact]
    public async Task Changing_the_setting_takes_effect_without_a_restart()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-live-setting") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        h.SettingsProvider.Current = Retention(autoDelete: false);
        RunCoordinator runs = h.Coordinator();
        Guid runId = await ClosedRunAsync(h, runs);

        clock.Advance(TimeSpan.FromDays(3));
        runs.Begin(h.AdditiveProfile(), null);
        Assert.Contains(runs.ListRuns(), r => r.RunId == runId);

        h.SettingsProvider.Current = Retention(autoDelete: true, hours: 1);
        runs.Begin(h.AdditiveProfile(), null);

        Assert.DoesNotContain(runs.ListRuns(), r => r.RunId == runId);
    }

    /// <summary>A LIVE run is never auto-deleted, whatever the clock says. Only the terminal timestamp makes
    /// a run eligible, and removing a run still doing work would strand it where nothing is tracking it.</summary>
    [Fact]
    public async Task A_live_run_is_never_auto_deleted()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-live") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        h.SettingsProvider.Current = Retention(autoDelete: true, hours: 1);
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        clock.Advance(TimeSpan.FromDays(7));
        runs.Begin(h.AdditiveProfile(), null);

        Assert.Contains(runs.ListRuns(), r => r.RunId == handle.RunId);
        Assert.Equal(RunPhase.AwaitingApproval, runs.GetStatus(handle.RunId)!.Phase);
    }

    // ---- the count backstop -------------------------------------------------------------------------

    /// <summary>What makes "never auto-delete" safe. The cap applies even with auto-delete off, and it
    /// evicts the OLDEST-closed first so what survives is what the user most likely still cares about.</summary>
    [Fact]
    public async Task The_count_cap_evicts_the_oldest_finished_runs_even_with_auto_delete_off()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-cap") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        h.SettingsProvider.Current = Retention(autoDelete: false);
        RunCoordinator runs = h.Coordinator(new EngineConfig { MaxRetainedClosedRuns = 3 });

        List<Guid> order = [];
        for (int i = 0; i < 5; i++)
        {
            order.Add(await ClosedRunAsync(h, runs));
            clock.Advance(TimeSpan.FromMinutes(1));   // distinct close times, so "oldest" is unambiguous
        }
        runs.Begin(h.AdditiveProfile(), null);   // a sweep

        IReadOnlyList<RunSummaryDto> listed = runs.ListRuns();
        Assert.DoesNotContain(listed, r => r.RunId == order[0]);
        Assert.DoesNotContain(listed, r => r.RunId == order[1]);
        foreach (Guid kept in order.Skip(2))
            Assert.Contains(listed, r => r.RunId == kept);
    }

    /// <summary>The cap counts only FINISHED runs and never evicts a live one, however many there are.</summary>
    [Fact]
    public async Task The_count_cap_never_evicts_a_live_run()
    {
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using RunPlanHarness h = new("retain-cap-live") { Time = clock };
        h.WriteSource("a.txt", "aaa");
        h.SettingsProvider.Current = Retention(autoDelete: false);
        RunCoordinator runs = h.Coordinator(new EngineConfig { MaxRetainedClosedRuns = 1 });

        // A parked run, then enough finished ones to blow the cap several times over.
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? live);
        await RunPlanHarness.WaitForPhaseAsync(runs, live!.RunId, RunPhase.AwaitingApproval);
        for (int i = 0; i < 4; i++)
        {
            await ClosedRunAsync(h, runs);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        IReadOnlyList<RunSummaryDto> listed = runs.ListRuns();
        Assert.Contains(listed, r => r.RunId == live.RunId);
        Assert.Equal(1, listed.Count(r => r.Phase == nameof(RunPhase.Closed)));
    }

    // ---- discard ------------------------------------------------------------------------------------

    /// <summary>The only user-driven deletion. Before this there was no way at all for a client to make the
    /// coordinator forget a run — the age sweep was the sole remover.</summary>
    [Fact]
    public async Task Discarding_a_finished_run_removes_it()
    {
        using RunPlanHarness h = new("discard-finished");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        Guid runId = await ClosedRunAsync(h, runs);
        Assert.Contains(runs.ListRuns(), r => r.RunId == runId);

        Assert.False(runs.Discard(runId).TryGetError(out _));

        Assert.Empty(runs.ListRuns());
        Assert.Null(runs.GetStatus(runId));
    }

    /// <summary>A parked plan is closed AND removed, so declining a preview does not leave a row behind for
    /// a plan the user said no to.</summary>
    [Fact]
    public async Task Discarding_a_parked_run_closes_it_and_removes_it()
    {
        using RunPlanHarness h = new("discard-parked");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        string? snapshot = runs.SnapshotDirectory(handle.RunId);
        Assert.True(Directory.Exists(snapshot));

        Assert.False(runs.Discard(handle.RunId).TryGetError(out _));

        Assert.Empty(runs.ListRuns());
        // The snapshot goes too — a discarded run must not leave its frozen work list on disk.
        Assert.False(Directory.Exists(snapshot));
    }

    /// <summary>Discarding an EXECUTING run cancels it first: the row goes at once, and cancel's own
    /// semantics still hold (pending work dropped, in-flight jobs finish, deletion phase skipped).</summary>
    [Fact]
    public async Task Discarding_an_executing_run_cancels_it_and_removes_it()
    {
        using RunPlanHarness h = new("discard-executing");
        for (int i = 0; i < 8; i++)
            h.WriteSource($"f{i}.txt", "x");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await Task.Delay(200);   // let the copies queue

        Assert.False(runs.Discard(handle.RunId).TryGetError(out _));

        Assert.Empty(runs.ListRuns());
        Assert.Equal(0, h.Queue.PendingCountForRun(handle.RunId));
    }

    [Fact]
    public void Discarding_an_unknown_run_is_an_error()
    {
        using RunPlanHarness h = new("discard-unknown");
        Assert.True(h.Coordinator().Discard(Guid.NewGuid()).TryGetError(out _));
    }

    /// <summary>Discarding twice is an error the second time, and that is the honest answer: the run really
    /// is gone. The UI treats RUN_NOT_FOUND as success for exactly this reason.</summary>
    [Fact]
    public async Task Discarding_the_same_run_twice_reports_the_second_as_unknown()
    {
        using RunPlanHarness h = new("discard-twice");
        h.WriteSource("a.txt", "aaa");
        RunCoordinator runs = h.Coordinator();
        Guid runId = await ClosedRunAsync(h, runs);

        Assert.False(runs.Discard(runId).TryGetError(out _));
        Assert.True(runs.Discard(runId).TryGetError(out _));
    }

    // ---- what a closed run still knows --------------------------------------------------------------

    /// <summary>`ReleaseAfterClose` frees the heavy members, so a finished run can be retained cheaply. It
    /// must not free anything the read models need: a retained run is only useful if it can still say what
    /// it did.</summary>
    [Fact]
    public async Task A_released_run_still_answers_with_everything_the_read_models_need()
    {
        using RunPlanHarness h = new("retain-released");
        h.WriteSource("a.txt", "12345");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        runs.Approve(handle.RunId, true);
        await h.DrainAndSettleAsync(runs, handle.RunId);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        RunStatus status = runs.GetStatus(handle.RunId)!;
        Assert.Equal(1, status.PlannedCopies);
        Assert.Equal(1, status.Succeeded);

        RunSummaryDto summary = Assert.Single(runs.ListRuns());
        // The profile is deliberately NOT released — Summarize needs its id and name, and a nameless row
        // is exactly the failure ProfileName was put on the wire to prevent.
        Assert.False(string.IsNullOrEmpty(summary.ProfileName));
        Assert.Equal(handle.ProfileId, summary.ProfileId);
        Assert.Equal(nameof(RunPhase.Closed), summary.Phase);
        Assert.NotNull(summary.ClosedAtUtc);
        Assert.NotNull(summary.PlannedAtUtc);
    }

    /// <summary>THE hazard in releasing a closed run, and the reason `ReleaseAfterClose` runs where it does.
    ///
    /// <para><c>PathsWritten</c> is the Mirror deletion pass's self-write guard: anything this run itself
    /// placed is off-limits however the plan described it, because conflict resolution can legitimately
    /// choose a different final path at execution time than the probe predicted at plan time. Clearing the
    /// set even one step too early would let the pass delete a file the run had just written.</para>
    ///
    /// <para>Driven by settling the copy onto the orphan's OWN path — exactly the drift the guard exists
    /// for. If the clear happened before the pass read the set, that file would be recycled.</para></summary>
    [Fact]
    public async Task A_path_this_run_wrote_is_never_deleted_even_when_the_plan_called_it_an_orphan()
    {
        using RunPlanHarness h = new("retain-selfwrite");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        RunCoordinator runs = h.Coordinator();
        runs.Begin(h.MirrorProfile(), null).TryGetValue(out RunHandle? handle);
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        Assert.Equal(1, runs.GetStatus(handle.RunId)!.PlannedDeletes);   // the plan DOES call it an orphan

        runs.Approve(handle.RunId, true);
        // The copy resolves to the orphan's own path, so the pass is asked to delete something this run
        // just wrote.
        await h.DrainAndSettleAsync(runs, handle.RunId, finalPaths: _ => [orphan]);
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        Assert.Equal(0, status.Deleted);
        Assert.True(File.Exists(orphan), "the pass must never delete a path this run wrote");
        Assert.Empty(h.Bin());
    }
}
