using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>The shell is the event router (it already owns every cross-view-model concern), so these
/// pin which child each event reaches.</summary>
public sealed class MainWindowViewModelEventRoutingTests
{
    private static readonly Guid ProfileId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static MainWindowViewModel NewShell(FakeIpcGateway gateway) =>
        new(gateway, new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"));

    private static JobSummaryDto Summary(Guid jobId, string outcome = "Succeeded") =>
        new(jobId, ProfileId, @"C:\src\a.txt", outcome, null, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));

    [Fact]
    public void Pause_changed_routes_to_the_status_bar()
    {
        MainWindowViewModel shell = NewShell(new FakeIpcGateway());

        shell.HandleEngineEvent(new PauseChangedEvent { AtUtc = DateTimeOffset.UnixEpoch, Paused = true });

        Assert.True(shell.StatusBar.IsPaused);
    }

    [Fact]
    public void Job_lifecycle_events_route_to_the_activity_view()
    {
        Guid jobId = Guid.NewGuid();
        MainWindowViewModel shell = NewShell(new FakeIpcGateway());

        shell.HandleEngineEvent(new JobStartedEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch, JobId = jobId, ProfileId = ProfileId, SourcePath = @"C:\src\a.txt",
        });
        Assert.True(Assert.Single(shell.Activity.Jobs).IsRunning);

        shell.HandleEngineEvent(new JobProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch, JobId = jobId,
            Phase = JobPhase.Distributing, TargetsCompleted = 1, TargetCount = 2,
        });
        Assert.Contains("1/2", Assert.Single(shell.Activity.Jobs).ProgressText);

        shell.HandleEngineEvent(new JobCompletedEvent { AtUtc = DateTimeOffset.UnixEpoch, Job = Summary(jobId) });
        Assert.Equal("Succeeded", Assert.Single(shell.Activity.Jobs).Outcome);
    }

    [Fact]
    public void A_failed_job_carries_its_residual_paths_into_the_row()
    {
        Guid jobId = Guid.NewGuid();
        MainWindowViewModel shell = NewShell(new FakeIpcGateway());

        shell.HandleEngineEvent(new JobFailedEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            Job = Summary(jobId, "RollbackFailed"),
            Error = "placement failed",
            NotifyOnFailure = true,
            ResidualPaths = [@"C:\left\behind.tmp"],
        });

        ActivityRow row = Assert.Single(shell.Activity.Jobs);
        Assert.True(row.HasResidualPaths);
        Assert.Equal("placement failed", row.Error);
    }

    [Fact]
    public void An_engine_warning_lands_on_the_activity_notice_not_the_profile_banner()
    {
        // The list banner is danger-styled and reserved for things the user must act on.
        MainWindowViewModel shell = NewShell(new FakeIpcGateway());

        shell.HandleEngineEvent(new EngineWarningEvent { AtUtc = DateTimeOffset.UnixEpoch, Message = "heads up" });

        Assert.Equal("heads up", shell.Activity.Notice);
        Assert.Null(shell.List.ErrorMessage);
    }

    [Theory]
    [InlineData(0, null, "Nothing in")]
    [InlineData(3, null, "Queued 3")]
    [InlineData(1, "drive vanished", "stopped")]
    public async Task A_run_queued_event_for_our_own_run_becomes_an_activity_notice(int count, string? error, string expected)
    {
        Guid runId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            GetResult = SingleSourceProfile(),
            RunProfileResult = new RunProfileResponse { RunId = runId },
        };
        MainWindowViewModel shell = NewShell(gateway);
        // A preview plans the editor's draft, so the editor has to be holding one for the run to start.
        shell.Editor.Load(SingleSourceProfile());
        await shell.PreviewProfileAsync(new ProfileListItem(ProfileId, "Photos", true, "Manual"));

        shell.HandleEngineEvent(RunQueued(runId, count, error));

        Assert.Contains(expected, shell.Activity.Notice);
    }

    /// <summary>The event bus broadcasts to every subscriber, so a run started by the CLI or a second
    /// window must not be announced here as though this user had asked for it.</summary>
    [Fact]
    public void A_run_queued_event_for_someone_elses_run_is_ignored()
    {
        MainWindowViewModel shell = NewShell(new FakeIpcGateway());

        shell.HandleEngineEvent(RunQueued(Guid.NewGuid(), 3, null));

        Assert.Null(shell.Activity.Notice);
    }

    private static RunQueuedEvent RunQueued(Guid runId, int count, string? error) => new()
    {
        AtUtc = DateTimeOffset.UnixEpoch,
        ProfileId = ProfileId,
        ScopePath = @"C:\in\a",
        QueuedCount = count,
        Error = error,
        RunId = runId,
    };

    private static Profile SingleSourceProfile() => ProfileFactory.Sample(ProfileId) with
    {
        Name = "Photos",
        Transformers = [],
        Sources = [new SourceConfig { Path = @"C:\in\a" }],
    };

    [Fact]
    public async Task Profiles_changed_refreshes_the_list()
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
                [new ProfileSummary(ProfileId, "Photos", true, "Manual")]),
        };
        MainWindowViewModel shell = NewShell(gateway);

        shell.HandleEngineEvent(new ProfilesChangedEvent { AtUtc = DateTimeOffset.UnixEpoch });
        await WaitUntilAsync(() => shell.List.Profiles.Count == 1);

        Assert.Equal("Photos", shell.List.Profiles[0].Name);
    }

    [Fact]
    public async Task Reconciling_engine_state_re_seeds_the_activity_view_and_the_status_bar()
    {
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(Guid.NewGuid())]),
            StatusResult = new EngineStatusSnapshot(Paused: true, 2, 0, 0, null),
        };
        MainWindowViewModel shell = NewShell(gateway);

        await shell.ReconcileEngineStateAsync();

        Assert.Single(shell.Activity.Jobs);
        Assert.True(shell.StatusBar.IsPaused);
        Assert.True(shell.StatusBar.IsConnected);
    }

    [Fact]
    public void Toggling_the_activity_panel_flips_visibility()
    {
        MainWindowViewModel shell = NewShell(new FakeIpcGateway());

        shell.ToggleActivity();
        Assert.True(shell.ActivityVisible);

        shell.ToggleActivity();
        Assert.False(shell.ActivityVisible);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }
}
