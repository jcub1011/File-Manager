using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Jobs;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

/// <summary>The run-profile / approve-run / cancel-run handlers.
///
/// <para>These handlers do almost nothing on purpose, and that is the property under test. A run's first
/// act is to scan and evaluate its whole source set, which cannot happen inside an IPC round trip
/// (§8 rule 5) — so the handler validates, delegates to <see cref="IRunCoordinator"/>, and returns.
/// What it must still do itself is refuse: an unknown profile, an inactive one, or a path outside every
/// Source has to be turned away here, with a code that says which, because the alternative is a run
/// that silently applies a profile's disposition (up to PermanentDelete) to a file the profile was
/// never configured to touch.</para></summary>
public sealed class RunHandlersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-runh-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;

    public RunHandlersTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Profile Profile() => TestProfiles.Valid(_sourceDir, Path.Combine(_root, "target"));

    private static (RunProfileHandler Handler, FakeRunCoordinator Runs) HandlerFor(Profile profile)
    {
        FakeRunCoordinator runs = new();
        return (new RunProfileHandler(
            new FakeProfileCatalog(profile), runs, NullLogger<RunProfileHandler>.Instance), runs);
    }

    // ---- accepting a run --------------------------------------------------------------------------

    [Fact]
    public async Task A_run_with_NO_PATH_covers_the_whole_profile()
    {
        Profile profile = Profile();
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id });

        RunProfileResponse run = Assert.IsType<RunProfileResponse>(response);
        Assert.NotEqual(Guid.Empty, run.RunId);
        // Null scope is what the GUI sends and what Mirror requires: an orphan can only be identified
        // over the COMPLETE source set.
        Assert.Null(Assert.Single(runs.Begun).ScopePath);
    }

    [Fact]
    public async Task A_run_scoped_to_a_file_under_a_source_is_accepted_and_records_its_scope()
    {
        Profile profile = Profile();
        string file = Path.Combine(_sourceDir, "a.txt");
        File.WriteAllText(file, "x");
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = profile.Id, Path = file });

        Assert.IsType<RunProfileResponse>(response);
        Assert.Equal(file, Assert.Single(runs.Begun).ScopePath);
    }

    [Fact]
    public async Task A_run_scoped_to_a_NESTED_folder_of_a_source_is_accepted()
    {
        Profile profile = Profile();
        string nested = Path.Combine(_sourceDir, "deep", "deeper");
        Directory.CreateDirectory(nested);
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = profile.Id, Path = nested });

        Assert.IsType<RunProfileResponse>(response);
        Assert.Single(runs.Begun);
    }

    // ---- refusals ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_profile_is_PROFILE_NOT_FOUND_and_starts_nothing()
    {
        Profile profile = Profile();
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = Guid.NewGuid(), Path = _sourceDir });

        Assert.Equal("PROFILE_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
        Assert.Empty(runs.Begun);
    }

    [Fact]
    public async Task An_INACTIVE_profile_stays_distinguishable_from_a_missing_one()
    {
        // Two different problems the user fixes two different ways, so collapsing them into one code
        // would leave "nothing happened and I don't know why".
        Profile inactive = Profile() with { Active = false };
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(inactive);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = inactive.Id, Path = _sourceDir });

        Assert.Equal("PROFILE_INACTIVE", Assert.IsType<ErrorResponse>(response).Code);
        Assert.Empty(runs.Begun);
    }

    [Fact]
    public async Task A_path_OUTSIDE_every_source_is_refused_and_starts_nothing()
    {
        Profile profile = Profile();
        string outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = profile.Id, Path = outside });

        // Containment is not optional: without it the run would apply the profile — and then its
        // OnSuccess disposition — to a file under no configured Source at all.
        Assert.Equal("PATH_OUT_OF_SCOPE", Assert.IsType<ErrorResponse>(response).Code);
        Assert.Empty(runs.Begun);
    }

    [Fact]
    public async Task A_path_that_does_not_exist_is_PATH_NOT_FOUND()
    {
        Profile profile = Profile();
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(profile);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest
        {
            ProfileId = profile.Id,
            Path = Path.Combine(_sourceDir, "not-there.txt"),
        });

        Assert.Equal("PATH_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
        Assert.Empty(runs.Begun);
    }

    [Fact]
    public async Task A_profile_with_no_sources_is_refused()
    {
        Profile empty = Profile() with { Sources = [] };
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(empty);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = empty.Id });

        Assert.Equal("PROFILE_NO_SOURCES", Assert.IsType<ErrorResponse>(response).Code);
        Assert.Empty(runs.Begun);
    }

    [Fact]
    public async Task A_coordinator_that_refuses_the_run_is_surfaced_not_swallowed()
    {
        Profile profile = Profile();
        FakeRunCoordinator runs = new() { BeginError = "the service is shutting down" };
        RunProfileHandler handler = new(
            new FakeProfileCatalog(profile), runs, NullLogger<RunProfileHandler>.Instance);

        IpcResponse response = await handler.HandleAsync(new RunProfileRequest { ProfileId = profile.Id });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("RUN_NOT_STARTED", error.Code);
        Assert.Contains("shutting down", error.Message, StringComparison.Ordinal);
    }

    // ---- approve / cancel -------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approve_forwards_the_decision_verbatim(bool approve)
    {
        FakeRunCoordinator runs = new();
        ApproveRunHandler handler = new(runs, NullLogger<ApproveRunHandler>.Instance);
        Guid runId = Guid.NewGuid();

        IpcResponse response = await handler.HandleAsync(
            new ApproveRunRequest { RunId = runId, Approve = approve });

        Assert.IsType<OkResponse>(response);
        Assert.Equal((runId, approve), Assert.Single(runs.Approvals));
    }

    [Fact]
    public async Task Approving_a_run_that_is_not_awaiting_approval_is_an_error()
    {
        FakeRunCoordinator runs = new() { ApproveError = "run is Executing, not awaiting approval" };
        ApproveRunHandler handler = new(runs, NullLogger<ApproveRunHandler>.Instance);

        IpcResponse response = await handler.HandleAsync(
            new ApproveRunRequest { RunId = Guid.NewGuid(), Approve = true });

        Assert.Equal("RUN_NOT_APPROVABLE", Assert.IsType<ErrorResponse>(response).Code);
    }

    [Fact]
    public async Task Cancel_forwards_the_run_id()
    {
        FakeRunCoordinator runs = new();
        CancelRunHandler handler = new(runs);
        Guid runId = Guid.NewGuid();

        IpcResponse response = await handler.HandleAsync(new CancelRunRequest { RunId = runId });

        Assert.IsType<OkResponse>(response);
        Assert.Equal(runId, Assert.Single(runs.Cancellations));
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_is_an_error()
    {
        FakeRunCoordinator runs = new() { CancelError = "no run with id" };
        CancelRunHandler handler = new(runs);

        IpcResponse response = await handler.HandleAsync(new CancelRunRequest { RunId = Guid.NewGuid() });

        Assert.Equal("RUN_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
    }

    /// <summary>Records what the handlers asked for, and can be scripted to refuse. Hand-written like
    /// every other double in this suite.</summary>
    private sealed class FakeRunCoordinator : IRunCoordinator
    {
        public List<(Guid ProfileId, string? ScopePath)> Begun { get; } = [];
        public List<(Guid RunId, bool Approve)> Approvals { get; } = [];
        public List<Guid> Cancellations { get; } = [];

        public string? BeginError { get; init; }
        public string? ApproveError { get; init; }
        public string? CancelError { get; init; }

        public Result<RunHandle, string> Begin(Profile profile, string? scopePath)
        {
            if (BeginError is not null)
                return BeginError;
            Begun.Add((profile.Id, scopePath));
            return new RunHandle { RunId = Guid.NewGuid(), ProfileId = profile.Id };
        }

        public Result Approve(Guid runId, bool approve)
        {
            if (ApproveError is not null)
                return ApproveError;
            Approvals.Add((runId, approve));
            return Result.Success();
        }

        public Result Cancel(Guid runId)
        {
            if (CancelError is not null)
                return CancelError;
            Cancellations.Add(runId);
            return Result.Success();
        }

        public RunStatus? GetStatus(Guid runId) => null;
        public void Settled(Guid runId, JobCompletion? completion) { }
        public void Coalesced(Guid runId) { }
        public string? SnapshotDirectory(Guid runId) => null;
        public Task StopAsync() => Task.CompletedTask;
    }
}
