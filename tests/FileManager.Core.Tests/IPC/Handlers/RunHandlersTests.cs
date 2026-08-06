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

    // ---- planning an unsaved draft ----------------------------------------------------------------
    // The GUI's Preview tab IS this request's planning phase, so it sends the profile on screen — unsaved
    // edits included — rather than asking the engine to resolve a persisted one that may differ.

    [Fact]
    public async Task An_INLINE_profile_is_planned_in_place_of_the_persisted_one()
    {
        Profile persisted = Profile();
        // Same id, different targets: if the handler resolved the catalog, the plan would be built from
        // the wrong destination and the user would approve a list of somewhere they never named.
        Profile draft = persisted with { Targets = [new TargetConfig { Path = Path.Combine(_root, "draft-target") }] };
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(persisted);

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = persisted.Id, InlineProfile = draft });

        Assert.IsType<RunProfileResponse>(response);
        Assert.Equal(draft, Assert.Single(runs.Begun).Profile);
    }

    [Fact]
    public async Task A_NEVER_SAVED_profile_can_be_run_from_its_draft_alone()
    {
        // There is no catalog entry to find, and that is the point: a brand-new profile is previewable
        // before it is ever saved.
        Profile draft = TestProfiles.Valid(_sourceDir, Path.Combine(_root, "target")) with { Id = Guid.NewGuid() };
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(Profile());

        IpcResponse response = await handler.HandleAsync(
            new RunProfileRequest { ProfileId = draft.Id, InlineProfile = draft });

        Assert.IsType<RunProfileResponse>(response);
        Assert.Equal(draft.Id, Assert.Single(runs.Begun).Profile.Id);
    }

    [Fact]
    public async Task Every_refusal_still_applies_to_an_INLINE_profile()
    {
        // A draft is not a way around the gates: an inactive or sourceless one is refused exactly as a
        // persisted one is, and the scope check reads the draft's own Sources.
        Profile persisted = Profile();
        (RunProfileHandler handler, FakeRunCoordinator runs) = HandlerFor(persisted);

        IpcResponse inactive = await handler.HandleAsync(new RunProfileRequest
        {
            ProfileId = persisted.Id,
            InlineProfile = persisted with { Active = false },
        });
        IpcResponse sourceless = await handler.HandleAsync(new RunProfileRequest
        {
            ProfileId = persisted.Id,
            InlineProfile = persisted with { Sources = [] },
        });

        Assert.Equal("PROFILE_INACTIVE", Assert.IsType<ErrorResponse>(inactive).Code);
        Assert.Equal("PROFILE_NO_SOURCES", Assert.IsType<ErrorResponse>(sourceless).Code);
        Assert.Empty(runs.Begun);
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

    // ---- get-runs / set-run-paused ---------------------------------------------------------------

    [Fact]
    public async Task Get_runs_answers_with_what_the_coordinator_holds()
    {
        RunSummaryDto run = new(
            Guid.NewGuid(), Guid.NewGuid(), "Photos", "Executing", "None",
            Paused: false, Waiting: false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null,
            10, 0, 1024, 0, 3, 0, 0, 0, false, null);
        FakeRunCoordinator runs = new() { Runs = [run] };
        GetRunsHandler handler = new(runs);

        IpcResponse response = await handler.HandleAsync(new GetRunsRequest());

        Assert.Equal(run, Assert.Single(Assert.IsType<RunsResponse>(response).Runs));
        Assert.Equal(1, runs.ListRunsCalls);
    }

    /// <summary>An idle engine answers an empty list, not an error — the same contract get-recent-jobs
    /// documents, and what lets a queue window show an honest empty state rather than a banner.</summary>
    [Fact]
    public async Task Get_runs_on_an_idle_engine_is_an_empty_list_not_an_error()
    {
        IpcResponse response = await new GetRunsHandler(new FakeRunCoordinator())
            .HandleAsync(new GetRunsRequest());

        Assert.Empty(Assert.IsType<RunsResponse>(response).Runs);
    }

    [Fact]
    public async Task Set_run_paused_passes_the_run_and_the_flag_through()
    {
        FakeRunCoordinator runs = new();
        SetRunPausedHandler handler = new(runs);
        Guid runId = Guid.NewGuid();

        Assert.IsType<OkResponse>(
            await handler.HandleAsync(new SetRunPausedRequest { RunId = runId, Paused = true }));
        Assert.IsType<OkResponse>(
            await handler.HandleAsync(new SetRunPausedRequest { RunId = runId, Paused = false }));

        Assert.Equal([(runId, true), (runId, false)], runs.PauseCalls);
    }

    /// <summary>RUN_NOT_FOUND — the same code cancel-run uses, deliberately: for a client whose rows come
    /// from a lossy stream, "that run is gone" is one situation, not two to learn.</summary>
    [Fact]
    public async Task Pausing_a_run_the_coordinator_refuses_is_RUN_NOT_FOUND()
    {
        FakeRunCoordinator runs = new() { SetPausedError = "no run with id" };
        SetRunPausedHandler handler = new(runs);

        IpcResponse response = await handler.HandleAsync(
            new SetRunPausedRequest { RunId = Guid.NewGuid(), Paused = true });

        Assert.Equal("RUN_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
    }

    [Fact]
    public async Task Discard_run_passes_the_id_through()
    {
        FakeRunCoordinator runs = new();
        DiscardRunHandler handler = new(runs);
        Guid runId = Guid.NewGuid();

        Assert.IsType<OkResponse>(await handler.HandleAsync(new DiscardRunRequest { RunId = runId }));

        Assert.Equal(runId, Assert.Single(runs.Discards));
    }

    /// <summary>RUN_NOT_FOUND, the same code cancel-run and set-run-paused use: for a client whose rows come
    /// from a lossy stream, "that run is gone" is one situation, not three to learn.</summary>
    [Fact]
    public async Task Discarding_a_run_the_coordinator_refuses_is_RUN_NOT_FOUND()
    {
        FakeRunCoordinator runs = new() { DiscardError = "no run with id" };
        DiscardRunHandler handler = new(runs);

        IpcResponse response = await handler.HandleAsync(new DiscardRunRequest { RunId = Guid.NewGuid() });

        Assert.Equal("RUN_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
    }

    // ---- get-run-detail ---------------------------------------------------------------------------
    // The summary a client shows beside a selected run. It reads the snapshot HEADER and nothing else,
    // which is what makes it cheap enough to issue on every selection change — the itemized alternative
    // (get-run-plan-stream) replays a work list that can hold half a million rows.

    /// <summary>The happy path, against a REAL snapshot written by the REAL planner. Scripting a header
    /// through a fake would prove only that the projection compiles; what has to hold is that the figures a
    /// client shows are the ones the planner actually recorded.</summary>
    [Fact]
    public async Task Get_run_detail_projects_the_snapshot_header()
    {
        using RunPlanHarness h = new("detail-happy");
        h.WriteSource("a.txt", "12345");
        h.WriteSource("b.txt", "678");
        h.WriteTarget("orphan.txt", "orphaned");
        Guid runId = Guid.NewGuid();
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), runId: runId);

        GetRunDetailHandler handler = new(new FakeRunCoordinator { SnapshotDirectoryResult = dir });
        IpcResponse response = await handler.HandleAsync(new GetRunDetailRequest { RunId = runId });

        RunDetailDto detail = Assert.IsType<RunDetailResponse>(response).Detail;
        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(runId, detail.RunId);
        Assert.Equal(2, detail.CopyItemCount);
        Assert.Equal(8, detail.CopyBytes);
        Assert.Equal(1, detail.DeleteItemCount);
        Assert.Equal(8, detail.DeleteBytes);
        Assert.Equal(header.SourceItemCount, detail.SourceItemCount);
        Assert.Equal(header.DestinationItemCount, detail.DestinationItemCount);
        Assert.Equal(header.OverwriteCount, detail.OverwriteCount);
        Assert.Equal(header.RenameCount, detail.RenameCount);
        Assert.Equal(header.DisposalCount, detail.DisposalCount);
        Assert.False(detail.Truncated);
        // The FROZEN profile, embedded in the snapshot — not a catalog lookup. This handler has no catalog
        // at all, which is the structural guarantee that it cannot answer with an edited revision.
        Assert.Equal(h.SourceDir, Assert.Single(detail.Profile.Sources).Path);
        Assert.Equal(SyncMode.Mirror, detail.Profile.SyncMode);
    }

    /// <summary>A scoped run says so on the wire. Worth carrying because a narrowed Mirror run copies but
    /// deletes NOTHING — its orphan set is unsound over a partial source tree — and no count on a summary
    /// reveals that; the deletion figure just reads zero.</summary>
    [Fact]
    public async Task Get_run_detail_reports_the_scope_a_narrowed_run_covered()
    {
        using RunPlanHarness h = new("detail-scope");
        string scoped = Path.Combine(h.SourceDir, "sub");
        Directory.CreateDirectory(scoped);
        h.WriteSource("sub/a.txt", "a");
        Guid runId = Guid.NewGuid();
        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(), scopePath: scoped, runId: runId);

        GetRunDetailHandler handler = new(new FakeRunCoordinator { SnapshotDirectoryResult = dir });
        IpcResponse response = await handler.HandleAsync(new GetRunDetailRequest { RunId = runId });

        Assert.Equal(scoped, Assert.IsType<RunDetailResponse>(response).Detail.ScopePath);
    }

    /// <summary>RUN_NOT_FOUND — the same code cancel-run, set-run-paused and discard-run use.</summary>
    [Fact]
    public async Task Get_run_detail_for_an_unknown_run_is_RUN_NOT_FOUND()
    {
        GetRunDetailHandler handler = new(new FakeRunCoordinator());   // no snapshot directory

        IpcResponse response = await handler.HandleAsync(
            new GetRunDetailRequest { RunId = Guid.NewGuid() });

        Assert.Equal("RUN_NOT_FOUND", Assert.IsType<ErrorResponse>(response).Code);
    }

    /// <summary>A run the coordinator HOLDS but which has not finished planning: the header is written when
    /// planning completes, so there is nothing to read yet.
    /// <para>RUN_PLAN_UNAVAILABLE rather than RUN_NOT_FOUND, and the distinction is the whole point — this
    /// is the phase a user is most likely to be watching, and a client must render it as "still working out
    /// what this will do" rather than as a failure or as a run that has vanished.</para></summary>
    [Fact]
    public async Task Get_run_detail_for_a_run_that_has_not_finished_planning_is_RUN_PLAN_UNAVAILABLE()
    {
        // A directory with no plan.json in it — exactly the state a run in Planning leaves behind.
        string empty = Path.Combine(_root, "still-planning");
        Directory.CreateDirectory(empty);
        GetRunDetailHandler handler = new(new FakeRunCoordinator { SnapshotDirectoryResult = empty });

        IpcResponse response = await handler.HandleAsync(
            new GetRunDetailRequest { RunId = Guid.NewGuid() });

        Assert.Equal("RUN_PLAN_UNAVAILABLE", Assert.IsType<ErrorResponse>(response).Code);
    }

    /// <summary>Records what the handlers asked for, and can be scripted to refuse. Hand-written like
    /// every other double in this suite.</summary>
    private sealed class FakeRunCoordinator : IRunCoordinator
    {
        // The whole profile, not just its id: run-profile may carry an unsaved draft to plan instead of
        // the persisted one, and "which profile actually reached the coordinator" is the point of those
        // cases.
        public List<(Profile Profile, string? ScopePath)> Begun { get; } = [];
        public List<(Guid RunId, bool Approve)> Approvals { get; } = [];

        /// <summary>The acknowledgment flag each approval carried — the handler's job is to pass the
        /// request's AcknowledgeWarnings through, and this is what makes that observable.</summary>
        public List<bool> Acknowledgements { get; } = [];
        public List<Guid> Cancellations { get; } = [];

        public string? BeginError { get; init; }
        public string? ApproveError { get; init; }
        public string? CancelError { get; init; }

        public Result<RunHandle, string> Begin(Profile profile, string? scopePath)
        {
            if (BeginError is not null)
                return BeginError;
            Begun.Add((profile, scopePath));
            return new RunHandle { RunId = Guid.NewGuid(), ProfileId = profile.Id };
        }

        public Result Approve(Guid runId, bool approve, bool acknowledgeWarnings = false)
        {
            if (ApproveError is not null)
                return ApproveError;
            Approvals.Add((runId, approve));
            Acknowledgements.Add(acknowledgeWarnings);
            return Result.Success();
        }

        public Result Cancel(Guid runId)
        {
            if (CancelError is not null)
                return CancelError;
            Cancellations.Add(runId);
            return Result.Success();
        }

        public List<Guid> Discards { get; } = [];
        public string? DiscardError { get; init; }

        public Result Discard(Guid runId)
        {
            if (DiscardError is not null)
                return DiscardError;
            Discards.Add(runId);
            return Result.Success();
        }

        public List<(Guid RunId, bool Paused)> PauseCalls { get; } = [];
        public string? SetPausedError { get; init; }

        public Result SetPaused(Guid runId, bool paused)
        {
            if (SetPausedError is not null)
                return SetPausedError;
            PauseCalls.Add((runId, paused));
            return Result.Success();
        }

        public IReadOnlyList<RunSummaryDto> Runs { get; init; } = [];
        public int ListRunsCalls { get; private set; }

        public IReadOnlyList<RunSummaryDto> ListRuns()
        {
            ListRunsCalls++;
            return Runs;
        }

        public RunStatus? GetStatus(Guid runId) => null;
        public void Settled(Guid runId, JobCompletion? completion) { }
        public Profile? PlannedProfile(Guid runId) => null;
        public void Coalesced(Guid runId) { }
        /// <summary>The run directory get-run-detail reads its header from. Null (the default) is a run the
        /// coordinator does not hold, which is what makes RUN_NOT_FOUND expressible.</summary>
        public string? SnapshotDirectoryResult { get; init; }

        public string? SnapshotDirectory(Guid runId) => SnapshotDirectoryResult;
        public Task StopAsync() => Task.CompletedTask;
    }
}
