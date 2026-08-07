using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>What the window does with a plan once the engine has one, and with the run's terminal report.
///
/// <para>The window is the only thing that turns a planned run into a real one. A run that plans and is
/// never answered does nothing at all AND leaves a snapshot directory parked on disk with no expiry, so
/// every path out of "planned" has to end in an answer or a displayed plan. The three that never reach
/// the Preview tab are pinned here: a failed plan, a plan with nothing to do, and someone else's
/// plan.</para></summary>
public sealed class MainWindowViewModelRunPlanTests
{
    private static readonly Guid ProfileId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static Profile MirrorProfile() => ProfileFactory.Sample(ProfileId) with
    {
        Name = "Backup",
        Transformers = [],
        SyncMode = SyncMode.Mirror,
        ScanDestination = true,
        Sources = [new SourceConfig { Path = @"C:\in" }],
    };

    private static ProfileListItem Row() => new(ProfileId, "Backup", true, "Manual");

    private static (MainWindowViewModel Shell, FakeIpcGateway Gateway) NewShell(Profile? profile = null)
    {
        FakeIpcGateway gateway = new() { GetResult = profile ?? MirrorProfile() };
        MainWindowViewModel shell = new(
            gateway, new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"));
        return (shell, gateway);
    }

    private static RunPlannedEvent Planned(
        Guid runId, int copies = 3, int deletes = 2, bool truncated = false, string? error = null) =>
        RunPlans.Planned(runId, ProfileId, copies, deletes, 3_000, 2_000, truncated, error);

    /// <summary>Starts a preview so the window is tracking the run's id, and returns that id. The editor
    /// must be holding the profile first: a preview plans the draft.</summary>
    private static async Task<Guid> StartPreviewAsync(MainWindowViewModel shell, FakeIpcGateway gateway)
    {
        shell.Editor.Load(MirrorProfile());
        Guid runId = Guid.NewGuid();
        gateway.RunProfileResult = new RunProfileResponse { RunId = runId };
        await shell.PreviewProfileAsync(Row());
        return runId;
    }

    // ---- the plan the user is shown ---------------------------------------------------------------

    [Fact]
    public async Task A_planned_run_streams_its_frozen_work_list_into_the_Preview_tab()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        // Read back from the RUN's snapshot, not re-planned: a fresh scan would produce a different list
        // from the one approving executes, which would defeat the entire point of showing it.
        Assert.Equal(runId, Assert.Single(gateway.RunPlanStreamCalls));
        Assert.Equal(runId, shell.DryRun.PendingRunId);
        Assert.Equal(3, shell.DryRun.PlannedCopies);
        Assert.Equal(2, shell.DryRun.PlannedDeletes);
        // Nothing has been answered — the footer is now the user's move.
        Assert.Empty(gateway.ApproveRunCalls);
    }

    [Fact]
    public async Task The_footer_states_the_DELETE_count_first_for_a_Mirror_plan()
    {
        // The deletion count is the most consequential number in the plan, and the one no other part of
        // the tab totals for the user.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId, copies: 3, deletes: 2));
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        Assert.StartsWith("2 file(s) to REMOVE", shell.DryRun.PlanSummary);
        Assert.Contains("Recycle Bin", shell.DryRun.PlanSummary);
    }

    [Fact]
    public async Task A_MIRROR_profile_warns_in_the_footer_that_the_run_deletes_at_the_target()
    {
        // Mirror's destructive half acts on the DESTINATION, so a footer naming only counts would describe
        // the safe part of the run and stay silent about the part that deletes.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        Assert.Contains("MIRROR", shell.DryRun.MirrorWarning);
        Assert.Contains("Recycle Bin", shell.DryRun.MirrorWarning);
        Assert.Contains("filters", shell.DryRun.MirrorWarning);
    }

    [Fact]
    public async Task A_TRUNCATED_plan_says_so_and_says_nothing_will_be_removed()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId, truncated: true));
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        Assert.True(shell.DryRun.PlanTruncated);
        Assert.Contains("may be incomplete", shell.DryRun.PlanTruncationNotice);
        // The engine refuses the deletion phase on a truncated plan, so promising otherwise here would be
        // a lie the user would only discover afterwards.
        Assert.Contains("No files will be removed", shell.DryRun.PlanTruncationNotice);
    }

    // ---- the superseding preview -------------------------------------------------------------------

    [Fact]
    public async Task A_second_preview_CANCELS_the_scan_the_first_one_started()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid first = await StartPreviewAsync(shell, gateway);

        // The user edits a filter and presses Preview again while the first scan is still walking.
        Guid second = Guid.NewGuid();
        gateway.RunProfileResult = new RunProfileResponse { RunId = second };
        await shell.PreviewProfileAsync(Row());

        // The superseded run must be CANCELLED, not merely forgotten: nulling the id left the engine
        // walking a tree nobody would ever answer for, holding a snapshot directory that nothing sweeps.
        Assert.Equal(first, Assert.Single(gateway.CancelRunCalls));
        Assert.Equal(second, shell.DryRun.PlanningRunId);
    }

    [Fact]
    public async Task A_superseded_run_s_LATE_plan_can_never_overwrite_the_newer_one()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid first = await StartPreviewAsync(shell, gateway);
        Guid second = Guid.NewGuid();
        gateway.RunProfileResult = new RunProfileResponse { RunId = second };
        await shell.PreviewProfileAsync(Row());

        // The newer plan lands...
        shell.HandleEngineEvent(Planned(second));
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);
        Assert.Equal(second, shell.DryRun.PendingRunId);

        // ...and then the abandoned scan finishes and publishes its own. It used to still be recognized as
        // ours, so its rows and PendingRunId replaced the newer plan's — and Approve then executed the
        // profile as it was BEFORE the edit that prompted the re-preview.
        shell.HandleEngineEvent(Planned(first));
        await Task.Delay(50);

        Assert.Equal(second, shell.DryRun.PendingRunId);
        Assert.Equal(second, Assert.Single(gateway.RunPlanStreamCalls));
    }

    // ---- the plan that arrives before its own reply ------------------------------------------------

    [Fact]
    public async Task A_plan_that_beats_its_own_run_profile_REPLY_is_still_shown()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        shell.Editor.Load(MirrorProfile());
        Guid runId = Guid.NewGuid();
        gateway.RunProfileResult = new RunProfileResponse { RunId = runId };

        // Planning is DETACHED on the service side and the event pump delivers on the UI thread, so a
        // profile whose plan takes a few milliseconds can have its run-planned dispatched while
        // PreviewProfileAsync is still suspended at its await. Dropping it left the tab on "Working out
        // what this will do…" forever, with the run parked in AwaitingApproval holding a snapshot.
        gateway.BeforeRunProfileReply = () => shell.HandleEngineEvent(Planned(runId));

        await shell.PreviewProfileAsync(Row());
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        Assert.Equal(runId, shell.DryRun.PendingRunId);
        Assert.Equal(runId, Assert.Single(gateway.RunPlanStreamCalls));
    }

    [Fact]
    public async Task A_buffered_plan_for_a_run_this_window_never_started_is_NOT_shown()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid theirs = Guid.NewGuid();

        // Another client's run. Buffering it must not become a way for it to be adopted later.
        shell.HandleEngineEvent(Planned(theirs));
        Guid ours = await StartPreviewAsync(shell, gateway);
        await Task.Delay(50);

        Assert.Null(shell.DryRun.PendingRunId);
        Assert.Empty(gateway.RunPlanStreamCalls);
        Assert.NotEqual(theirs, ours);
    }

    // ---- progress while planning -------------------------------------------------------------------

    [Fact]
    public async Task Planning_progress_reaches_the_TAB_and_not_the_activity_panel()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(new RunProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            Phase = "Planning",
            Completed = 0,
            Total = 0,
            Deleted = 0,
            ScannedSources = 12_345,
            ScannedDestinations = 6_000,
        });

        // The activity panel is closed until a run is approved, so a scan's progress posted only there is
        // progress the user never sees — and planning is exactly when they are waiting on it.
        Assert.Contains("12,345", shell.DryRun.RunStatusText, StringComparison.Ordinal);
        Assert.Contains("6,000", shell.DryRun.RunStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("12,345", shell.Activity.Notice ?? "", StringComparison.Ordinal);
    }

    /// <summary>The stage and the unreadable count reach the tab too, not just the two totals.
    ///
    /// <para>The stage is what stops the caption reading as a stall: once the walk ends, its findings are
    /// written into the work list with both counts frozen at their final values, and a caption still saying
    /// "scanning sources… 12,345 found" describes something that is no longer happening.</para></summary>
    [Fact]
    public async Task The_planning_stage_and_unreadable_count_reach_the_tabs_caption()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(new RunProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            Phase = "Planning",
            Completed = 0,
            Total = 0,
            Deleted = 0,
            ScannedSources = 12_345,
            UnreadableEntries = 7,
            PlanStage = RunPlanStages.Building,
        });

        Assert.StartsWith("Building the plan…", shell.DryRun.RunStatusText, StringComparison.Ordinal);
        Assert.Contains("12,345", shell.DryRun.RunStatusText, StringComparison.Ordinal);
        Assert.Contains("7 unreadable", shell.DryRun.RunStatusText, StringComparison.Ordinal);
    }

    /// <summary>A preview queued behind the concurrent-plan limit says so on the tab.
    ///
    /// <para>The coordinator publishes that sample under its own phase string, and its counters cannot move
    /// while the run is parked. Routed as execution progress it became "Running: 0 of 0 file(s)…" in a panel
    /// that is closed until a run is approved, while the tab the user IS looking at sat on the caption it
    /// opened with — a scan apparently stuck at zero, for as long as the run ahead took to plan.</para></summary>
    [Fact]
    public async Task A_preview_queued_behind_the_plan_limit_says_so_on_the_tab()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(new RunProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            Phase = "Waiting",
            Completed = 0,
            Total = 0,
            Deleted = 0,
            PlanStage = RunPlanStages.Scanning,
        });

        Assert.Equal("Waiting for another preview to finish…", shell.DryRun.RunStatusText);
        Assert.DoesNotContain("Running", shell.Activity.Notice ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_progress_still_goes_to_the_activity_panel()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(new RunProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            Phase = "Executing",
            Completed = 7,
            Total = 10,
            Deleted = 2,
        });

        Assert.Contains("7 of 10", shell.Activity.Notice);
        Assert.Contains("2 removed", shell.Activity.Notice);
    }

    // ---- the plans that never reach the tab -------------------------------------------------------

    [Fact]
    public async Task A_plan_with_NOTHING_to_do_is_closed_without_showing_a_footer()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId, copies: 0, deletes: 0));
        await WaitUntilAsync(() => gateway.ApproveRunCalls.Count == 1);

        // Nothing worth approving — but the run still has to be closed or it sits pending with a snapshot
        // on disk that nothing will ever answer for.
        Assert.Equal((runId, false), gateway.ApproveRunCalls[0]);
        Assert.Null(shell.DryRun.PendingRunId);
        Assert.Empty(gateway.RunPlanStreamCalls);
        Assert.Contains("Nothing to do", shell.Activity.Notice);
        // And on the tab the preview just navigated to: the activity panel is closed until a run is
        // approved, so a notice that lives only there is one the user never sees.
        Assert.Contains("Nothing to do", shell.DryRun.EmptyStateText);
        Assert.False(shell.DryRun.IsPreviewing);
    }

    [Fact]
    public async Task A_FAILED_plan_is_reported_and_never_streamed()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId, error: "source \"C:\\in\" is unreadable"));
        await WaitUntilAsync(() => shell.DryRun.ErrorMessage is not null);

        // The coordinator already closed the run, so there is nothing to answer and nothing to show.
        Assert.Empty(gateway.ApproveRunCalls);
        Assert.Empty(gateway.RunPlanStreamCalls);
        Assert.Contains("unreadable", shell.DryRun.ErrorMessage);
        Assert.Null(shell.DryRun.PendingRunId);
        Assert.False(shell.DryRun.IsPreviewing);
    }

    [Fact]
    public async Task A_plan_for_ANOTHER_clients_run_is_ignored()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(Guid.NewGuid()));   // not ours
        await Task.Delay(100);

        // The bus is a broadcast. Showing — let alone offering to approve — a run this user never started
        // would be this window authorising someone else's file deletions.
        Assert.Empty(gateway.ApproveRunCalls);
        Assert.Empty(gateway.RunPlanStreamCalls);
        Assert.Null(shell.DryRun.PendingRunId);
    }

    // ---- abandonment -----------------------------------------------------------------------------

    [Fact]
    public async Task Closing_the_window_declines_a_plan_left_waiting()
    {
        // A pending run holds a snapshot directory and has no expiry of its own, so a preview the user
        // walked away from would leak one for the lifetime of the service.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == runId);

        await shell.RequestCloseAsync();

        Assert.Contains((runId, false), gateway.ApproveRunCalls);
    }

    // ---- the terminal report ----------------------------------------------------------------------

    [Fact]
    public async Task The_completion_notice_reports_copies_and_removals()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(new RunCompletedEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = ProfileId,
            Outcome = "Succeeded",
            Succeeded = 812,
            Skipped = 3,
            Failed = 0,
            Deleted = 14,
            BytesDeleted = 2048,
        });

        Assert.Contains("812 copied", shell.Activity.Notice);
        Assert.Contains("14 removed", shell.Activity.Notice);
    }

    [Fact]
    public async Task A_run_whose_deletions_were_REFUSED_says_so_in_the_completion_notice()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(new RunCompletedEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = ProfileId,
            Outcome = "CompletedWithProblems",
            Succeeded = 811,
            Skipped = 0,
            Failed = 1,
            Deleted = 0,
            BytesDeleted = 0,
            DeletionAbortReason = "1 file could not be copied",
        });

        // The copies mostly worked and the destructive half did not run: the destination is NOT a mirror
        // of the source, and the user has to be told that in the same breath as the success count.
        Assert.Contains("1 FAILED", shell.Activity.Notice);
        Assert.Contains("No files were removed", shell.Activity.Notice);
    }

    // ---- retained previews ------------------------------------------------------------------------

    /// <summary>The change this feature is made of. Looking away from a profile used to DECLINE its run,
    /// destroying the result; now the run stays parked so the rows can be re-streamed from its snapshot.</summary>
    [Fact]
    public async Task Switching_profiles_keeps_the_preview_result_instead_of_declining_it()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == runId);

        shell.DryRun.ClearProfile();     // what selecting a different profile does to the tab

        Assert.Empty(gateway.DiscardRunCalls);              // the run was NOT discarded
        Assert.Equal(runId, shell.Previews.For(ProfileId)?.RunId);
        Assert.Null(shell.DryRun.PendingRunId);             // nothing on screen to approve
    }

    /// <summary>And coming back re-streams it. Note the second plan-stream call: the rows are re-fetched
    /// from the snapshot rather than kept in memory, which is what stops N retained previews costing N ×
    /// a few hundred megabytes.</summary>
    [Fact]
    public async Task Returning_to_a_profile_re_streams_its_retained_preview()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == runId);

        // Away and back, through the list's selection — the path that actually loads a profile and so the
        // only one that can restore its preview. Deselecting first because ProfileListItem is a record:
        // re-assigning an equal instance is a no-op and would never re-fire the selection.
        ProfileListItem row = Row();
        shell.List.Profiles.Add(row);
        shell.List.SelectedProfile = null;
        await WaitUntilAsync(() => !shell.Editor.HasProfile);
        Assert.Null(shell.DryRun.PendingRunId);

        shell.List.SelectedProfile = row;
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        Assert.Equal(runId, shell.DryRun.PendingRunId);
        Assert.Equal(2, gateway.RunPlanStreamCalls.Count(id => id == runId));
        Assert.True(shell.DryRun.HasReport);
        Assert.Empty(gateway.DiscardRunCalls);
    }

    /// <summary>Re-previewing the SAME profile supersedes its own retained result, and the run that result
    /// held must be DISCARDED — not merely declined. Declining closes a run, and a closed run is now kept
    /// until something removes it, so declining would leave the queue holding a row for a preview the user
    /// superseded and never asked to keep.</summary>
    [Fact]
    public async Task Re_previewing_a_profile_discards_the_run_its_retained_result_was_holding()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid first = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(first));
        // Waits on the STORE, not on PendingRunId. ShowRunPlanAsync sets the footer inside LoadPlanAsync and
        // only remembers the result after it returns, so a wait on the footer can proceed while the store is
        // still empty — and then the second preview finds nothing to supersede and discards nothing.
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == first);

        Guid second = Guid.NewGuid();
        gateway.RunProfileResult = new RunProfileResponse { RunId = second };
        await shell.PreviewProfileAsync(Row());
        shell.HandleEngineEvent(Planned(second));
        // And waits on the DISCARD, which is what this asserts. Remember discards before it stores, but
        // these run on a pool thread here (the app resumes them on the UI thread), so waiting on the store
        // and asserting the call reads two unsynchronized writes in the wrong order.
        await WaitUntilAsync(() => gateway.DiscardRunCalls.Count > 0);

        Assert.Equal(first, Assert.Single(gateway.DiscardRunCalls));
        Assert.Equal(second, shell.Previews.For(ProfileId)?.RunId);
    }

    /// <summary>Window close is the last chance to release every retained run's snapshot directory, and
    /// there may now be several rather than one. Discarded, not declined, so they leave the queue too.</summary>
    [Fact]
    public async Task Closing_the_window_discards_every_retained_preview()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == runId);
        // A second profile's retained result, so this covers the many case the store exists for.
        Guid otherProfile = Guid.NewGuid(), otherRun = Guid.NewGuid();
        shell.Previews.Remember(otherProfile, new StoredPreview(
            RunPlans.Planned(otherRun, otherProfile), DateTimeOffset.UnixEpoch));

        await shell.RequestCloseAsync();

        Assert.Contains(runId, gateway.DiscardRunCalls);
        Assert.Contains(otherRun, gateway.DiscardRunCalls);
        Assert.Equal(0, shell.Previews.Count);
    }

    /// <summary>A run that has ENDED is no longer restorable, and must be dropped WITHOUT discarding it —
    /// the user may still want its row in the queue, which is the whole point of keeping finished runs.
    /// Forgetting the retained PREVIEW is not the same as deleting the RUN.</summary>
    [Fact]
    public async Task A_retained_preview_whose_run_finished_is_forgotten_silently()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == runId);

        shell.HandleEngineEvent(new RunCompletedEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = ProfileId,
            Outcome = "Succeeded",
            Succeeded = 3,
            Skipped = 0,
            Failed = 0,
            Deleted = 2,
            BytesDeleted = 2_000,
        });

        Assert.Null(shell.Previews.For(ProfileId));
        Assert.Empty(gateway.DiscardRunCalls);
    }

    // ---- the job queue ----------------------------------------------------------------------------

    /// <summary>The queue takes EVERY run, ours or not — that is what makes it a queue rather than a second
    /// copy of this window's state. The ownership filter governs only what the Preview tab shows.</summary>
    [Fact]
    public async Task The_queue_shows_another_clients_run_even_though_the_preview_tab_ignores_it()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        await StartPreviewAsync(shell, gateway);
        Guid theirs = Guid.NewGuid();

        shell.HandleEngineEvent(Planned(theirs));

        JobQueueRow row = Assert.Single(shell.Queue.Runs);
        Assert.Equal(theirs, row.RunId);
        // Shown, but not approvable here: nobody approves work they have not looked at.
        Assert.False(row.CanApproveHere);
        Assert.True(row.IsAwaitingApproval);
        Assert.Null(shell.DryRun.PendingRunId);
    }

    /// <summary>Our own run's row IS approvable, because this window showed its plan.</summary>
    [Fact]
    public async Task The_queue_offers_approve_for_a_plan_this_window_showed()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

        Assert.True(Assert.Single(shell.Queue.Runs).CanApproveHere);
    }

    /// <summary>Approving from the QUEUE while the tab is showing that same run must route through the
    /// tab's own footer — the acknowledgment checkbox for the run's blocking warnings lives there, and
    /// answering around it would send acknowledgeWarnings: false for a run the user just ticked.</summary>
    [Fact]
    public async Task Approving_from_the_queue_routes_through_the_tab_when_it_is_showing_that_run()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartPreviewAsync(shell, gateway);
        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.Previews.For(ProfileId)?.RunId == runId);
        shell.DryRun.AcknowledgedWarnings = true;

        await shell.Queue.ApproveCommand.ExecuteAsync(shell.Queue.Runs[0]);

        Assert.Equal((runId, true), Assert.Single(gateway.ApproveRunCalls));
        Assert.True(Assert.Single(gateway.ApproveRunAcknowledgements));
        Assert.Null(shell.Previews.For(ProfileId));   // the answer consumed the retained result
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }
}
