using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
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
        await WaitUntilAsync(() => shell.DryRun.PendingRunId is not null);

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }
}
