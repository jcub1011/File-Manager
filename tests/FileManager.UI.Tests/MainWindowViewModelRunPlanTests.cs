using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>The approval half of a run: what the user is shown once the plan is known, and what the
/// window does with their answer.
///
/// <para>The window is the only thing that turns a planned run into a real one. A run that plans and is
/// never approved does nothing at all, so an approval path that silently drops a plan is a feature that
/// appears broken; and an approval prompt that fails to mention destination deletions is how a Mirror
/// run's destructive half reaches the disk unannounced. Both are pinned here.</para></summary>
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
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"))
        {
            ConfirmRunProfile = _ => Task.FromResult(true),
        };
        return (shell, gateway);
    }

    private static RunPlannedEvent Planned(
        Guid runId, int copies = 3, int deletes = 2, bool truncated = false, string? error = null) => new()
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            RunId = runId,
            ProfileId = ProfileId,
            PlannedCopies = copies,
            PlannedDeletes = deletes,
            PlannedCopyBytes = 3_000,
            PlannedDeleteBytes = 2_000,
            Truncated = truncated,
            Error = error,
        };

    /// <summary>Starts a run so the window is tracking its id, and returns that id.</summary>
    private static async Task<Guid> StartRunAsync(MainWindowViewModel shell, FakeIpcGateway gateway)
    {
        Guid runId = Guid.NewGuid();
        gateway.RunProfileResult = new RunProfileResponse { RunId = runId };
        await shell.RunProfileNowAsync(Row());
        return runId;
    }

    // ---- the confirmation the user answers before the scan ----------------------------------------

    [Fact]
    public async Task The_pre_run_confirmation_warns_that_a_MIRROR_run_deletes_at_the_target()
    {
        string? shown = null;
        (MainWindowViewModel shell, _) = NewShell();
        shell.ConfirmRunProfile = message => { shown = message; return Task.FromResult(false); };

        await shell.RunProfileNowAsync(Row());

        // Mirror's destructive half acts on the DESTINATION. A dialog naming only the source
        // disposition describes the safe half of the run and stays silent about the half that deletes.
        Assert.Contains("MIRROR", shown);
        Assert.Contains("RECYCLE BIN", shown);
        // And the consequence that will otherwise be reported as data loss: an excluded source file
        // contributes no survivor, so tightening a filter removes copies made earlier.
        Assert.Contains("filters", shown);
    }

    [Fact]
    public async Task An_ADDITIVE_profile_confirmation_does_not_mention_deleting_at_the_target()
    {
        string? shown = null;
        (MainWindowViewModel shell, _) = NewShell(MirrorProfile() with { SyncMode = SyncMode.AdditiveArchive });
        shell.ConfirmRunProfile = message => { shown = message; return Task.FromResult(false); };

        await shell.RunProfileNowAsync(Row());

        Assert.DoesNotContain("MIRROR", shown);
    }

    [Fact]
    public async Task The_pre_run_confirmation_says_nothing_happens_yet()
    {
        string? shown = null;
        (MainWindowViewModel shell, _) = NewShell();
        shell.ConfirmRunProfile = message => { shown = message; return Task.FromResult(false); };

        await shell.RunProfileNowAsync(Row());

        // Saying so is what makes the first dialog answerable: the user is agreeing to find out, not to
        // move files.
        Assert.Contains("Nothing is copied or deleted yet", shown);
    }

    // ---- the approval the user answers after the scan ---------------------------------------------

    [Fact]
    public async Task A_planned_run_is_APPROVED_when_the_user_agrees()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = _ => Task.FromResult(true);

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => gateway.ApproveRunCalls.Count == 1);

        Assert.Equal((runId, true), gateway.ApproveRunCalls[0]);
    }

    [Fact]
    public async Task A_planned_run_is_DECLINED_when_the_user_says_no()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = _ => Task.FromResult(false);

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => gateway.ApproveRunCalls.Count == 1);

        // Declining must be sent, not merely not-approved: the service is holding a planned run and a
        // frozen work list on disk, and only an answer closes it.
        Assert.Equal((runId, false), gateway.ApproveRunCalls[0]);
    }

    [Fact]
    public async Task The_approval_prompt_names_the_copy_and_DELETE_counts()
    {
        string? shown = null;
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = message => { shown = message; return Task.FromResult(false); };

        shell.HandleEngineEvent(Planned(runId, copies: 812, deletes: 14));
        await WaitUntilAsync(() => shown is not null);

        Assert.Contains("812", shown);
        // The number that matters: this is the only place the user sees how many files leave the target.
        Assert.Contains("14", shown);
        Assert.Contains("REMOVE", shown);
        Assert.Contains("Recycle Bin", shown);
    }

    [Fact]
    public async Task An_approval_prompt_with_no_deletions_does_not_mention_removing_anything()
    {
        string? shown = null;
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = message => { shown = message; return Task.FromResult(false); };

        shell.HandleEngineEvent(Planned(runId, copies: 5, deletes: 0));
        await WaitUntilAsync(() => shown is not null);

        Assert.DoesNotContain("REMOVE", shown);
    }

    [Fact]
    public async Task A_TRUNCATED_plan_says_so_and_says_nothing_will_be_removed()
    {
        string? shown = null;
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = message => { shown = message; return Task.FromResult(false); };

        shell.HandleEngineEvent(Planned(runId, copies: 5, deletes: 3, truncated: true));
        await WaitUntilAsync(() => shown is not null);

        Assert.Contains("may be incomplete", shown);
        // The engine refuses the deletion phase on a truncated plan, so promising otherwise here would
        // be a lie the user would only discover afterwards.
        Assert.Contains("No files will be removed", shown);
    }

    [Fact]
    public async Task A_plan_with_NOTHING_to_do_is_closed_without_asking()
    {
        bool asked = false;
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = _ => { asked = true; return Task.FromResult(true); };

        shell.HandleEngineEvent(Planned(runId, copies: 0, deletes: 0));
        await WaitUntilAsync(() => gateway.ApproveRunCalls.Count == 1);

        // Nothing to approve, so nothing to interrupt the user for — but the run still has to be closed
        // or it sits pending with a snapshot on disk.
        Assert.False(asked);
        Assert.Equal((runId, false), gateway.ApproveRunCalls[0]);
    }

    [Fact]
    public async Task A_FAILED_plan_is_reported_and_never_asked_about()
    {
        bool asked = false;
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = _ => { asked = true; return Task.FromResult(true); };

        shell.HandleEngineEvent(Planned(runId, error: "source \"C:\\in\" is unreadable"));
        await WaitUntilAsync(() => shell.List.ErrorMessage is not null);

        Assert.False(asked);
        Assert.Empty(gateway.ApproveRunCalls);
        Assert.Contains("unreadable", shell.List.ErrorMessage);
    }

    [Fact]
    public async Task A_plan_for_ANOTHER_clients_run_is_ignored()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = _ => Task.FromResult(true);

        shell.HandleEngineEvent(Planned(Guid.NewGuid()));   // not ours
        await Task.Delay(100);

        // The bus is a broadcast. Approving a run this user never started would be this window
        // authorising someone else's file deletions.
        Assert.Empty(gateway.ApproveRunCalls);
    }

    [Fact]
    public async Task A_null_approval_callback_proceeds_so_headless_runs_are_not_blocked()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = null;

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => gateway.ApproveRunCalls.Count == 1);

        Assert.Equal((runId, true), gateway.ApproveRunCalls[0]);
    }

    [Fact]
    public async Task A_failed_approve_request_lands_in_the_banner()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);
        shell.ConfirmRunPlan = _ => Task.FromResult(true);
        gateway.ApproveRunResult = new IpcError("RUN_NOT_APPROVABLE", "run is Closed");

        shell.HandleEngineEvent(Planned(runId));
        await WaitUntilAsync(() => shell.List.ErrorMessage is not null);

        Assert.Contains("Closed", shell.List.ErrorMessage);
    }

    // ---- the terminal report ----------------------------------------------------------------------

    [Fact]
    public async Task The_completion_notice_reports_copies_and_removals()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        Guid runId = await StartRunAsync(shell, gateway);

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
        Guid runId = await StartRunAsync(shell, gateway);

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
