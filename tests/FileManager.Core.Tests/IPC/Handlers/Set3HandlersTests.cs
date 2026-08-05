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