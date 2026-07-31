using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

public sealed class Set3HandlersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-h-" + Guid.NewGuid().ToString("N"));

    public Set3HandlersTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GetMatching_returns_active_profiles_whose_source_contains_the_path()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        FilterCompiler filterCompiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
        ProfileMatcher matcher = new(new FakeProfileCatalog(profile), filterCompiler, NullLogger<ProfileMatcher>.Instance);
        GetMatchingProfilesHandler handler = new(matcher);

        IpcResponse response = await handler.HandleAsync(new GetMatchingProfilesRequest { Path = sourceDir });

        MatchingProfilesResponse matching = Assert.IsType<MatchingProfilesResponse>(response);
        Assert.Single(matching.Matches);
        Assert.Equal(profile.Id, matching.Matches[0].ProfileId);
    }

    [Fact]
    public async Task SetPaused_delegates_to_the_pause_service()
    {
        FakePauseState pause = new();
        SetPausedHandler handler = new(pause, NullLogger<SetPausedHandler>.Instance);

        IpcResponse response = await handler.HandleAsync(new SetPausedRequest { Paused = true });

        Assert.IsType<OkResponse>(response);
        Assert.True(pause.IsPaused);
    }

    [Fact]
    public async Task RunProfile_on_a_file_reports_one_queued_and_no_scan()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        string file = Path.Combine(sourceDir, "a.txt");
        File.WriteAllText(file, "x");
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        (RunProfileHandler handler, TriggerQueue queue, RecordingEventBus bus) = RunHandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = file });

        RunProfileResponse run = Assert.IsType<RunProfileResponse>(response);
        Assert.Equal(1, run.QueuedCount);
        Assert.False(run.Scanning);
        Assert.Equal(1, queue.PendingCount);
        // A single-file run's count is already exact, so a run-queued event would double-count.
        Assert.DoesNotContain(bus.Events, e => e is RunQueuedEvent);
    }

    [Fact]
    public async Task RunProfile_refuses_an_inactive_profile()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        string file = Path.Combine(sourceDir, "a.txt");
        File.WriteAllText(file, "x");
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target")) with { Active = false };
        (RunProfileHandler handler, TriggerQueue queue, _) = RunHandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = file });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("PROFILE_INACTIVE", error.Code);
        Assert.Equal(0, queue.PendingCount);
    }

    /// <summary>The folder path inherits containment from the scanner, which refuses an out-of-scope
    /// scope path with a Fatal fault. The file path must refuse it too: it used to fall back to the
    /// file's OWN directory as the source root and run the profile — copying the file to every target
    /// and then applying OnSuccess (up to PermanentDelete) to a file the profile never covered.</summary>
    [Fact]
    public async Task RunProfile_refuses_a_file_outside_every_source()
    {
        string sourceDir = Path.Combine(_root, "source");
        string elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(elsewhere);
        string strayFile = Path.Combine(elsewhere, "taxes.pdf");
        File.WriteAllText(strayFile, "x");
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        (RunProfileHandler handler, TriggerQueue queue, RecordingEventBus bus) = RunHandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = profile.Id, Path = strayFile });

        Assert.Equal("PATH_OUT_OF_SCOPE", Assert.IsType<ErrorResponse>(response).Code);
        Assert.Equal(0, queue.PendingCount);
        Assert.Empty(bus.Events);
        Assert.True(File.Exists(strayFile));
    }

    /// <summary>A file directly AT a source root is in scope — the boundary the containment check must
    /// not over-refuse (NormalizedPath treats "equals the root" as contained).</summary>
    [Fact]
    public async Task RunProfile_accepts_a_file_in_a_nested_folder_of_a_source()
    {
        string sourceDir = Path.Combine(_root, "source");
        string nested = Path.Combine(sourceDir, "deep", "deeper");
        Directory.CreateDirectory(nested);
        string file = Path.Combine(nested, "a.txt");
        File.WriteAllText(file, "x");
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        (RunProfileHandler handler, TriggerQueue queue, _) = RunHandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = file });

        Assert.Equal(1, Assert.IsType<RunProfileResponse>(response).QueuedCount);
        Assert.Equal(1, queue.PendingCount);
    }

    /// <summary>The reply's RunId must be the one the later event carries, or a client cannot tell its
    /// own run's outcome from another client's on a broadcast bus.</summary>
    [Fact]
    public async Task RunProfile_correlates_its_reply_with_the_run_queued_event()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        (RunProfileHandler handler, _, RecordingEventBus bus) = RunHandlerFor(profile, new FakeSourceScanner());

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = sourceDir });

        RunProfileResponse run = Assert.IsType<RunProfileResponse>(response);
        Assert.NotEqual(Guid.Empty, run.RunId);
        RunQueuedEvent queued = await WaitForRunQueuedAsync(bus);
        Assert.Equal(run.RunId, queued.RunId);
    }

    /// <summary>A log that exists but cannot be read is NOT "no log". The UI renders JOB_LOG_NOT_FOUND
    /// as the benign "skipped before any work began" and suppresses its error banner, so collapsing
    /// both failures into that code told users the opposite of the truth about a job that had failed.</summary>
    [Fact]
    public async Task GetJobLog_distinguishes_a_missing_log_from_an_unreadable_one()
    {
        Guid missing = Guid.NewGuid();
        Guid unreadable = Guid.NewGuid();
        GetJobLogHandler handler = new(new StubJobLogStore(missing, unreadable));

        IpcResponse absent = await handler.HandleAsync(new GetJobLogRequest { JobId = missing });
        IpcResponse locked = await handler.HandleAsync(new GetJobLogRequest { JobId = unreadable });

        Assert.Equal("JOB_LOG_NOT_FOUND", Assert.IsType<ErrorResponse>(absent).Code);
        Assert.Equal("JOB_LOG_UNREADABLE", Assert.IsType<ErrorResponse>(locked).Code);
    }

    /// <summary>Answers NotFound for one job id and Unreadable for another; every other member throws,
    /// so the test cannot pass by accident through an unrelated path.</summary>
    private sealed class StubJobLogStore(Guid missing, Guid unreadable) : IJobLogStore
    {
        public Result Append(Guid jobId, string line) => throw new NotSupportedException();

        public Result<IReadOnlyList<string>, JobLogReadError> Read(Guid jobId)
        {
            if (jobId == missing)
                return new JobLogReadError(JobLogReadFailure.NotFound, $"no log for job {jobId}");
            if (jobId == unreadable)
                return new JobLogReadError(JobLogReadFailure.Unreadable, "could not read the job log: locked");
            throw new NotSupportedException();
        }

        public Result<IReadOnlyList<JobSummary>, string> ListRecent(int count) => throw new NotSupportedException();

        public void RecordSummary(JobSummary summary) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RunProfile_for_a_missing_profile_stays_distinguishable_from_inactive()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        (RunProfileHandler handler, _, _) = RunHandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = Guid.NewGuid(), Path = sourceDir });

        Assert.Equal("PROFILE_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
    }

    [Fact]
    public async Task RunProfile_on_a_folder_replies_scanning_then_publishes_the_queued_count()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        Payload one = new(profile.Id, Path.Combine(sourceDir, "a.txt"), sourceDir, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);
        Payload two = new(profile.Id, Path.Combine(sourceDir, "b.txt"), sourceDir, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);
        (RunProfileHandler handler, _, RecordingEventBus bus) = RunHandlerFor(profile, new FakeSourceScanner(one, two));

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = sourceDir });

        RunProfileResponse run = Assert.IsType<RunProfileResponse>(response);
        Assert.True(run.Scanning);
        Assert.Equal(0, run.QueuedCount);   // not knowable at reply time — the event carries it

        RunQueuedEvent queued = await WaitForRunQueuedAsync(bus);
        Assert.Equal(2, queued.QueuedCount);
        Assert.Equal(profile.Id, queued.ProfileId);
        Assert.Equal(sourceDir, queued.ScopePath);
        Assert.Null(queued.Error);
    }

    [Fact]
    public async Task RunProfile_on_a_folder_that_matches_nothing_publishes_a_zero_count()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        (RunProfileHandler handler, _, RecordingEventBus bus) = RunHandlerFor(profile, new FakeSourceScanner());

        await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = sourceDir });

        // "Nothing matched" is the case a bare Ok reply could never report.
        RunQueuedEvent queued = await WaitForRunQueuedAsync(bus);
        Assert.Equal(0, queued.QueuedCount);
        Assert.Null(queued.Error);
    }

    [Fact]
    public async Task RunProfile_on_a_folder_whose_scan_aborts_publishes_the_fault()
    {
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        Payload one = new(profile.Id, Path.Combine(sourceDir, "a.txt"), sourceDir, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);
        FakeSourceScanner scanner = new(one, new EnumerationFault("drive vanished", EnumerationSeverity.Fatal));
        (RunProfileHandler handler, _, RecordingEventBus bus) = RunHandlerFor(profile, scanner);

        await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = sourceDir });

        RunQueuedEvent queued = await WaitForRunQueuedAsync(bus);
        Assert.Equal("drive vanished", queued.Error);
        Assert.Equal(1, queued.QueuedCount);   // partial count of what was queued before the abort
    }

    [Fact]
    public async Task RunProfile_reports_directories_the_scan_could_not_read()
    {
        // SourceScanner downgrades an unopenable subdirectory to a Warning so its siblings are still
        // walked, and RunQueuedEvent.Error is reserved for a Fatal. Without the warning event the
        // notice reads "Queued 1 file(s) from ..." over a tree that was only partly covered — and the
        // user may then approve a run whose OnSuccess is PermanentDelete.
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        Payload one = new(profile.Id, Path.Combine(sourceDir, "a.txt"), sourceDir, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);
        FakeSourceScanner scanner = new(
            new EnumerationFault("subdirectory skipped: access denied", EnumerationSeverity.Warning), one);
        (RunProfileHandler handler, _, RecordingEventBus bus) = RunHandlerFor(profile, scanner);

        await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = sourceDir });

        RunQueuedEvent queued = await WaitForRunQueuedAsync(bus);
        Assert.Equal(1, queued.QueuedCount);   // the walk continued past the warning
        Assert.Null(queued.Error);             // ...so it is not a Fatal, and Error stays null

        EngineWarningEvent warning = Assert.Single(bus.Events.OfType<EngineWarningEvent>());
        Assert.Contains("1 item(s)", warning.Message);
        Assert.Contains(sourceDir, warning.Message);
    }

    /// <summary>Yields payloads until its token is cancelled, so a test can observe what a shutdown does
    /// to a walk that is still in flight.</summary>
    private sealed class BlockingScanner(Payload payload, ManualResetEventSlim started) : ISourceScanner
    {
        public IEnumerable<Result<Payload, EnumerationFault>> Scan(
            Profile profile, TriggerKind trigger, string? scopeRoot = null, CancellationToken ct = default)
        {
            yield return payload;
            started.Set();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(5);
            }
        }
    }

    [Fact]
    public async Task Disposing_the_handler_stops_an_in_flight_folder_walk()
    {
        // The walk used to run under CancellationToken.None with no owner: closing the app (which shuts
        // the service down in StartAndStopWithProgram mode) left it enumerating and holding
        // ScanScheduler's LongRunning threads until the process died.
        string sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        Profile profile = TestProfiles.Valid(sourceDir, Path.Combine(_root, "target"));
        Payload one = new(profile.Id, Path.Combine(sourceDir, "a.txt"), sourceDir, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);
        using ManualResetEventSlim started = new(false);
        (RunProfileHandler handler, _, RecordingEventBus bus) =
            RunHandlerFor(profile, new BlockingScanner(one, started));

        await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id, Path = sourceDir });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the walk never started");

        handler.Dispose();

        // The run still terminates — RunQueuedEvent is the only way a caller that got Scanning=true
        // learns the outcome — carrying the partial count and an error rather than a clean zero.
        RunQueuedEvent queued = await WaitForRunQueuedAsync(bus);
        Assert.Equal(1, queued.QueuedCount);
        Assert.Contains("shut down", queued.Error);
    }

    private (RunProfileHandler Handler, TriggerQueue Queue, RecordingEventBus Bus) RunHandlerFor(
        Profile profile, ISourceScanner? scanner = null)
    {
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance);
        RecordingEventBus bus = new();
        RunProfileHandler handler = new(
            new FakeProfileCatalog(profile), queue, scanner ?? new FakeSourceScanner(), bus,
            TimeProvider.System, NullLogger<RunProfileHandler>.Instance);
        return (handler, queue, bus);
    }

    /// <summary>The folder path enumerates on a background task, so the terminating event is awaited
    /// rather than assumed present when HandleAsync returns.</summary>
    private static async Task<RunQueuedEvent> WaitForRunQueuedAsync(RecordingEventBus bus)
    {
        for (int i = 0; i < 200; i++)
        {
            if (bus.Events.OfType<RunQueuedEvent>().FirstOrDefault() is { } found)
                return found;
            await Task.Delay(25);
        }
        Assert.Fail("no run-queued event was published");
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public async Task GetJobLog_for_an_unknown_job_is_an_error()
    {
        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        Directory.CreateDirectory(paths.JobLogsDirectory);
        JobLogStore store = new(paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);
        GetJobLogHandler handler = new(store);

        IpcResponse response = await handler.HandleAsync(new GetJobLogRequest { JobId = Guid.NewGuid() });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("JOB_LOG_NOT_FOUND", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
