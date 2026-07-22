using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Filtering;
using FileManager.Core.IPC.Handlers;
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
