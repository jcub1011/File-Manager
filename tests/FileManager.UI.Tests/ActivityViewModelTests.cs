using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class ActivityViewModelTests
{
    private static readonly Guid ProfileId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static JobSummaryDto Summary(
        Guid jobId, string outcome = "Succeeded", string? skipReason = null, string path = @"C:\src\a.txt") =>
        new(jobId, ProfileId, path, outcome, skipReason, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(2));

    private static JobStartedEvent Started(Guid jobId, string path = @"C:\src\a.txt") =>
        new() { AtUtc = DateTimeOffset.UnixEpoch, JobId = jobId, ProfileId = ProfileId, SourcePath = path };

    [Theory]
    [InlineData("Succeeded", null, "Succeeded", "IconCheckmark", "Brush.Success")]
    [InlineData("Skipped", "Filtered", "filtered out", "IconSkip", "Brush.Muted")]
    [InlineData("Skipped", "SourceDisposed", "source no longer there", "IconSkip", "Brush.Muted")]
    [InlineData("Skipped", "UnchangedAtAllTargets", "already up to date", "IconSkip", "Brush.Muted")]
    [InlineData("Failed", null, "Failed", "IconWarning", "Brush.Danger")]
    [InlineData("RollbackFailed", null, "rollback incomplete", "IconWarning", "Brush.Danger")]
    [InlineData("SomethingNew", null, "SomethingNew", "IconQuestion", "Brush.Muted")]
    public async Task Reconcile_maps_each_outcome_string(
        string outcome, string? skipReason, string expectedText, string expectedIcon, string expectedColor)
    {
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success(
                [Summary(jobId, outcome, skipReason)]),
        };
        ActivityViewModel activity = new(gateway);

        await activity.ReconcileAsync();

        ActivityRow row = Assert.Single(activity.Jobs);
        Assert.Contains(expectedText, row.StatusText);
        Assert.Equal(expectedIcon, row.StatusIconKey);
        Assert.Equal(expectedColor, row.StatusColorKey);
        Assert.True(activity.HasJobs);
    }

    [Fact]
    public void Job_started_prepends_a_running_row_and_completion_updates_it_in_place()
    {
        Guid jobId = Guid.NewGuid();
        ActivityViewModel activity = new(new FakeIpcGateway());

        activity.OnJobStarted(Started(jobId));
        ActivityRow row = Assert.Single(activity.Jobs);
        Assert.True(row.IsRunning);

        activity.OnJobFinished(Summary(jobId), null, null);

        // Same instance mutated, so the row does not jump in the list.
        Assert.Same(row, Assert.Single(activity.Jobs));
        Assert.False(row.IsRunning);
        Assert.Equal("Succeeded", row.Outcome);
        Assert.Null(row.ProgressText);
    }

    [Fact]
    public void A_duplicate_job_started_does_not_add_a_second_row()
    {
        Guid jobId = Guid.NewGuid();
        ActivityViewModel activity = new(new FakeIpcGateway());

        activity.OnJobStarted(Started(jobId));
        activity.OnJobStarted(Started(jobId));

        Assert.Single(activity.Jobs);
    }

    [Fact]
    public void Completion_for_an_unknown_job_still_lands()
    {
        // job-started can be dropped by the subscriber's bounded channel; the terminal event is
        // authoritative and must not be lost with it.
        ActivityViewModel activity = new(new FakeIpcGateway());

        activity.OnJobFinished(Summary(Guid.NewGuid()), null, null);

        Assert.Single(activity.Jobs);
        Assert.False(Assert.Single(activity.Jobs).IsRunning);
    }

    [Fact]
    public void Rollback_failure_surfaces_the_residual_paths()
    {
        Guid jobId = Guid.NewGuid();
        ActivityViewModel activity = new(new FakeIpcGateway());
        activity.OnJobStarted(Started(jobId));

        activity.OnJobFinished(Summary(jobId, "RollbackFailed"), "placement failed", [@"C:\left\behind.tmp"]);

        ActivityRow row = Assert.Single(activity.Jobs);
        Assert.True(row.HasResidualPaths);
        Assert.Contains(@"C:\left\behind.tmp", row.ResidualPathsText);
        Assert.Equal("placement failed", row.Error);
    }

    [Fact]
    public void Progress_updates_the_running_row()
    {
        Guid jobId = Guid.NewGuid();
        ActivityViewModel activity = new(new FakeIpcGateway());
        activity.OnJobStarted(Started(jobId));

        activity.OnProgress(new JobProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            JobId = jobId,
            Phase = JobPhase.Distributing,
            TargetsCompleted = 1,
            TargetCount = 3,
        });

        Assert.Contains("1/3", Assert.Single(activity.Jobs).ProgressText);
    }

    [Fact]
    public void Progress_for_an_unknown_job_is_ignored()
    {
        ActivityViewModel activity = new(new FakeIpcGateway());

        activity.OnProgress(new JobProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            JobId = Guid.NewGuid(),
            Phase = JobPhase.Distributing,
            TargetsCompleted = 1,
            TargetCount = 1,
        });

        Assert.Empty(activity.Jobs);
    }

    [Fact]
    public async Task Reconcile_keeps_still_running_rows_the_ring_does_not_mention()
    {
        // get-recent-jobs is the COMPLETED-job ring, so a naive full replace would delete in-flight rows.
        Guid finished = Guid.NewGuid();
        Guid running = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(finished)]),
        };
        ActivityViewModel activity = new(gateway);
        activity.OnJobStarted(Started(running, @"C:\src\live.txt"));

        await activity.ReconcileAsync();

        Assert.Equal(2, activity.Jobs.Count);
        Assert.Contains(activity.Jobs, r => r.JobId == running && r.IsRunning);
        Assert.Contains(activity.Jobs, r => r.JobId == finished);
    }

    /// <summary>The collection is newest-first, so a preserved live row belongs at the HEAD. Appending it
    /// after up to 100 completed rows dropped the running job — and its live progress caption, the whole
    /// reason the panel exists — below the fold on the ordinary path of opening the panel mid-run. It also
    /// made that row the first casualty of the tail trim.</summary>
    [Fact]
    public async Task Reconcile_puts_a_preserved_running_row_at_the_head()
    {
        Guid running = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success(
                [.. Enumerable.Range(0, 20).Select(_ => Summary(Guid.NewGuid()))]),
        };
        ActivityViewModel activity = new(gateway);
        activity.OnJobStarted(Started(running, @"C:\src\live.txt"));

        await activity.ReconcileAsync();

        Assert.Equal(running, activity.Jobs[0].JobId);
        Assert.True(activity.Jobs[0].IsRunning);
    }

    /// <summary>A row the event stream already flipped terminal while the get-recent-jobs response was in
    /// flight is in neither set: the ring answered before the summary was recorded, and the row is no
    /// longer IsRunning. An IsRunning-only filter deleted it, so the job the user just triggered vanished
    /// from the feed with no completed row and no way to open its log.</summary>
    [Fact]
    public async Task Reconcile_keeps_a_row_that_went_terminal_while_the_request_was_in_flight()
    {
        Guid raced = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(Guid.NewGuid())]),
        };
        ActivityViewModel activity = new(gateway);
        activity.OnJobStarted(Started(raced, @"C:\src\raced.txt"));
        activity.OnJobFinished(Summary(raced), null, null);   // the completion event won the race

        await activity.ReconcileAsync();

        ActivityRow kept = Assert.Single(activity.Jobs, r => r.JobId == raced);
        Assert.Equal("Succeeded", kept.Outcome);
    }

    /// <summary>Reconcile rebuilds every row from the wire DTO, so the selected job comes back as a new
    /// instance for the same JobId. That fired the selection-changed hook and re-fetched an identical log
    /// on every reconnect, Refresh and panel open.</summary>
    [Fact]
    public async Task Reconcile_does_not_refetch_the_log_for_an_unchanged_selection()
    {
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(jobId)]),
        };
        gateway.JobLogResults[jobId] = Result<IReadOnlyList<string>, IpcError>.Success(["opened", "committed"]);
        ActivityViewModel activity = new(gateway);
        await activity.ReconcileAsync();

        activity.SelectedJob = activity.Jobs[0];
        await WaitForLogAsync(activity);
        Assert.Single(gateway.JobLogCalls);

        await activity.ReconcileAsync();
        await activity.ReconcileAsync();

        Assert.Single(gateway.JobLogCalls);                       // still one fetch, not three
        Assert.Equal(jobId, activity.SelectedJob!.JobId);         // and the selection survived
        Assert.Equal(["opened", "committed"], activity.LogLines);
    }

    /// <summary>A notice has no other writer that retires it, so without a dismiss affordance one
    /// "nothing matched" message stayed on screen for the rest of the session.</summary>
    [Fact]
    public async Task A_notice_can_be_dismissed_and_a_reconcile_clears_it()
    {
        ActivityViewModel activity = new(new FakeIpcGateway());

        activity.ShowNotice("Nothing in C:\\in matched this profile.");
        activity.DismissNoticeCommand.Execute(null);
        Assert.Null(activity.Notice);

        activity.ShowNotice("Queued 3 file(s).");
        await activity.ReconcileAsync();
        Assert.Null(activity.Notice);
    }

    /// <summary>The log load runs fire-and-forget from the selection-changed hook, so it is awaited
    /// rather than assumed complete.</summary>
    private static async Task WaitForLogAsync(ActivityViewModel activity)
    {
        for (int i = 0; i < 200 && activity.LogStatus == "Loading…"; i++)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Reconcile_with_an_empty_ring_is_not_an_error()
    {
        // The service's ring is in-memory and wiped on restart, so empty is a legitimate answer.
        ActivityViewModel activity = new(new FakeIpcGateway());

        await activity.ReconcileAsync();

        Assert.Empty(activity.Jobs);
        Assert.False(activity.HasJobs);
        Assert.Null(activity.ErrorMessage);
    }

    [Fact]
    public async Task Reconcile_failure_keeps_the_existing_rows()
    {
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(jobId)]),
        };
        ActivityViewModel activity = new(gateway);
        await activity.ReconcileAsync();

        gateway.RecentJobsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe");
        await activity.ReconcileAsync();

        Assert.Single(activity.Jobs);   // a transient outage must not blank the panel
        Assert.Contains("no pipe", activity.ErrorMessage);
    }

    [Fact]
    public async Task Selecting_a_job_loads_its_log()
    {
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(jobId)]),
        };
        gateway.JobLogResults[jobId] = Result<IReadOnlyList<string>, IpcError>.Success(["opened", "committed"]);
        ActivityViewModel activity = new(gateway);
        await activity.ReconcileAsync();

        activity.SelectedJob = activity.Jobs[0];
        await WaitUntilAsync(() => activity.LogLines.Count == 2);

        Assert.Equal(["opened", "committed"], activity.LogLines);
        Assert.Null(activity.LogStatus);
    }

    [Fact]
    public async Task A_missing_job_log_reads_as_no_log_not_an_error()
    {
        // Jobs skipped before their journal opened never write a log file. That is normal.
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success(
                [Summary(jobId, "Skipped", "SourceDisposed")]),
            JobLogResult = new IpcError("JOB_LOG_NOT_FOUND", "no log for job"),
        };
        ActivityViewModel activity = new(gateway);
        await activity.ReconcileAsync();

        activity.SelectedJob = activity.Jobs[0];
        await WaitUntilAsync(() => activity.LogStatus is not null && !activity.LogStatus.Contains("Loading"));

        Assert.Contains("No log for this job", activity.LogStatus);
        Assert.Null(activity.ErrorMessage);
    }

    [Fact]
    public async Task A_job_log_transport_failure_sets_the_error_banner()
    {
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(jobId)]),
            JobLogResult = new IpcError("IPC_TRANSPORT", "pipe broke"),
        };
        ActivityViewModel activity = new(gateway);
        await activity.ReconcileAsync();

        activity.SelectedJob = activity.Jobs[0];
        await WaitUntilAsync(() => activity.ErrorMessage is not null);

        Assert.Contains("pipe broke", activity.ErrorMessage);
    }

    [Fact]
    public async Task A_stale_log_response_does_not_land_on_a_newer_selection()
    {
        Guid slow = Guid.NewGuid();
        Guid quick = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success(
                [Summary(slow, path: @"C:\src\slow.txt"), Summary(quick, path: @"C:\src\quick.txt")]),
        };
        TaskCompletionSource slowGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.JobLogGates[slow] = slowGate;
        gateway.JobLogResults[slow] = Result<IReadOnlyList<string>, IpcError>.Success(["STALE"]);
        gateway.JobLogResults[quick] = Result<IReadOnlyList<string>, IpcError>.Success(["FRESH"]);
        ActivityViewModel activity = new(gateway);
        await activity.ReconcileAsync();

        activity.SelectedJob = activity.Jobs.Single(r => r.JobId == slow);      // starts, blocks
        activity.SelectedJob = activity.Jobs.Single(r => r.JobId == quick);     // supersedes it
        await WaitUntilAsync(() => activity.LogLines.Contains("FRESH"));
        slowGate.SetResult();                                                  // stale response lands late
        await Task.Delay(50);

        Assert.Equal(["FRESH"], activity.LogLines);
    }

    [Fact]
    public void Rows_are_capped_at_the_maximum()
    {
        ActivityViewModel activity = new(new FakeIpcGateway());

        for (int i = 0; i < ActivityViewModel.MaxRows + 50; i++)
            activity.OnJobStarted(Started(Guid.NewGuid(), $@"C:\src\{i}.txt"));

        Assert.Equal(ActivityViewModel.MaxRows, activity.Jobs.Count);
    }

    [Fact]
    public async Task Profile_names_are_resolved_through_the_shell_lookup()
    {
        Guid jobId = Guid.NewGuid();
        FakeIpcGateway gateway = new()
        {
            RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([Summary(jobId)]),
        };
        ActivityViewModel activity = new(gateway) { ProfileNameLookup = _ => "Photos" };

        await activity.ReconcileAsync();

        Assert.Equal("Photos", Assert.Single(activity.Jobs).ProfileName);
    }

    /// <summary>The log fetch is kicked off fire-and-forget from the selection change (mirroring the
    /// shell's LoadSelectionSafeAsync), so tests poll rather than await it.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }
}
