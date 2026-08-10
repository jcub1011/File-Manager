using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using System.Runtime.CompilerServices;

namespace FileManager.UI.Tests.Fakes;

/// <summary>Scripted gateway: tests assign the result fields and inspect the recorded calls.</summary>
internal sealed class FakeIpcGateway : IIpcGateway
{
    public List<(Profile Profile, bool Acknowledge)> SaveCalls { get; } = [];
    public List<Guid> DeleteCalls { get; } = [];
    public List<GlobalSettings> SaveSettingsCalls { get; } = [];
    public List<(string NewDirectory, bool MoveExisting)> RelocateCalls { get; } = [];

    public Result<EngineStatusSnapshot, IpcError> StatusResult { get; set; } =
        new EngineStatusSnapshot(false, 0, 0, 0, null);

    public Result<IReadOnlyList<ProfileSummary>, IpcError> ListResult { get; set; } =
        Result<IReadOnlyList<ProfileSummary>, IpcError>.Success([]);

    public Result<Profile, IpcError> GetResult { get; set; } =
        new IpcError("PROFILE_NOT_FOUND", "not scripted");

    public Result<SaveOutcome, IpcError> SaveResult { get; set; } =
        new SaveOutcome(true, []);

    public Result<bool, IpcError> DeleteResult { get; set; } = true;

    /// <summary>The row set a test expects to see. Named for the report shape rather than the request:
    /// there is no dry-run request left on this seam, and <see cref="RunPlanResults"/> is where this is
    /// replayed from. Kept because a whole <c>DryRunReport</c> is still the readable way to describe an
    /// expected set of rows.</summary>
    public Result<DryRunReport, IpcError> DryRunResult
    {
        get => _dryRunResult;
        set { _dryRunResult = value; DryRunResultSet = true; }
    }

    /// <summary>Whether a test scripted a plan at all. The paged verbs serve one only when it did:
    /// otherwise <see cref="Pages"/> and <see cref="ViewResult"/> are in charge (the paging-machinery
    /// tests script individual pages), and run detail keeps answering "still planning", which is what the
    /// job-queue tests are about.</summary>
    public bool DryRunResultSet { get; private set; }

    private Result<DryRunReport, IpcError> _dryRunResult =
        new DryRunReport
        {
            ProfileId = Guid.Empty,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = [],
            SourceFiles = [],
            DestinationFiles = [],
            SourceOperations = [],
            DestinationOperations = [],
        };

    public Result<GlobalSettings, IpcError> GetSettingsResult { get; set; } = GlobalSettings.Default;
    public Result<GlobalSettings, IpcError> SaveSettingsResult { get; set; } = GlobalSettings.Default;
    public Result<RelocateProfilesResponse, IpcError> RelocateResult { get; set; } =
        new RelocateProfilesResponse { Settings = GlobalSettings.Default };
    public Result<bool, IpcError> ShutdownResult { get; set; } = true;
    public int ShutdownCalls { get; private set; }

    public Task<Result<EngineStatusSnapshot, IpcError>> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(StatusResult);

    public Task<Result<IReadOnlyList<ProfileSummary>, IpcError>> ListProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult(ListResult);

    public Task<Result<Profile, IpcError>> GetProfileAsync(Guid profileId, CancellationToken ct = default) =>
        Task.FromResult(GetResult);

    public Task<Result<SaveOutcome, IpcError>> SaveProfileAsync(
        Profile profile, bool acknowledgeWarnings, CancellationToken ct = default)
    {
        SaveCalls.Add((profile, acknowledgeWarnings));
        return Task.FromResult(SaveResult);
    }

    public Task<Result<bool, IpcError>> DeleteProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        DeleteCalls.Add(profileId);
        return Task.FromResult(DeleteResult);
    }

    public Task<Result<GlobalSettings, IpcError>> GetSettingsAsync(CancellationToken ct = default) =>
        Task.FromResult(GetSettingsResult);

    public Task<Result<GlobalSettings, IpcError>> SaveSettingsAsync(GlobalSettings settings, CancellationToken ct = default)
    {
        SaveSettingsCalls.Add(settings);
        return Task.FromResult(SaveSettingsResult);
    }

    public Task<Result<RelocateProfilesResponse, IpcError>> RelocateProfilesAsync(
        string newDirectory, bool moveExisting, CancellationToken ct = default)
    {
        RelocateCalls.Add((newDirectory, moveExisting));
        return Task.FromResult(RelocateResult);
    }

    public Task<Result<bool, IpcError>> ShutdownServiceAsync(CancellationToken ct = default)
    {
        ShutdownCalls++;
        // A stopped service stops answering. Without this the switchover's verify loop would keep
        // seeing the service it just shut down, which is precisely the race it exists to catch.
        if (ShutdownResult.IsSuccess && StopMakesTheServiceUnreachable)
            StatusResult = new IpcError("SERVICE_UNAVAILABLE", "stopped");
        return Task.FromResult(ShutdownResult);
    }

    /// <summary>Whether a successful shutdown should make <see cref="GetStatusAsync"/> start failing.
    /// True by default because that is what really happens; a test wanting to model a service that
    /// refuses to die sets it false.</summary>
    public bool StopMakesTheServiceUnreachable { get; set; } = true;

    public int ResetConnectionCalls { get; private set; }

    /// <summary>The <c>allowStart</c> argument of every <see cref="ResetConnectionAsync"/> call, in
    /// order — how a test asserts that a polling caller stops asking for process launches.</summary>
    public List<bool> ResetConnectionAllowStart { get; } = [];

    /// <summary>What <see cref="GetStatusAsync"/> starts answering once the connection is reset —
    /// i.e. what the newly launched executable reports. Null leaves the current result alone.</summary>
    public Result<EngineStatusSnapshot, IpcError>? StatusAfterReset { get; set; }

    public Task ResetConnectionAsync(bool allowStart = true)
    {
        ResetConnectionCalls++;
        ResetConnectionAllowStart.Add(allowStart);
        if (StatusAfterReset is { } next)
            StatusResult = next;
        return Task.CompletedTask;
    }

    // ---- live single-job surface ------------------------------------------------------------------

    public List<(Guid ProfileId, string? Path)> RunProfileCalls { get; } = [];

    public Result<RunProfileResponse, IpcError> RunProfileResult { get; set; } =
        new RunProfileResponse { RunId = Guid.NewGuid() };

    /// <summary>Per-path override, so one test can script an accepted root and a failing one.</summary>
    public Dictionary<string, Result<RunProfileResponse, IpcError>> RunProfileResults { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<bool> SetPausedCalls { get; } = [];
    public Result<bool, IpcError> SetPausedResult { get; set; } = true;

    /// <summary>When set, SetPausedAsync awaits this before returning (poll-race tests). Mirrors
    /// <see cref="RunPlanGate"/>.</summary>
    public TaskCompletionSource? SetPausedGate { get; set; }

    public List<int> RecentJobsCalls { get; } = [];
    public Result<IReadOnlyList<JobSummaryDto>, IpcError> RecentJobsResult { get; set; } =
        Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success([]);

    public List<Guid> JobLogCalls { get; } = [];
    public Result<IReadOnlyList<string>, IpcError> JobLogResult { get; set; } =
        new IpcError("JOB_LOG_NOT_FOUND", "not scripted");
    public Dictionary<Guid, Result<IReadOnlyList<string>, IpcError>> JobLogResults { get; } = [];

    /// <summary>Per-job gate, so a stale-response test can resolve selections out of order.</summary>
    public Dictionary<Guid, TaskCompletionSource> JobLogGates { get; } = [];

    /// <summary>One entry per SubscribeEventsAsync attempt: each is yielded in order and then the
    /// stream ends, so a pump under test sees N connect/disconnect cycles.</summary>
    public Queue<IReadOnlyList<Result<EngineEvent, IpcError>>> SubscribeSegments { get; } = new();
    public int SubscribeCalls { get; private set; }

    /// <summary>Awaited once <see cref="SubscribeSegments"/> is exhausted so the pump PARKS rather
    /// than hot-looping reconnects for the rest of the test. Null parks until cancellation.</summary>
    public TaskCompletionSource? SubscribeIdleGate { get; set; }

    /// <summary>The inline draft passed to each RunProfileAsync call (null when the persisted-profile
    /// path was used), recorded in lockstep with <see cref="RunProfileCalls"/>. The preview flow always
    /// sends a draft, so "which profile is this run planning" is asserted from here.</summary>
    public List<Profile?> RunProfileDrafts { get; } = [];

    /// <summary>Invoked after the call is recorded but BEFORE the reply is returned, so a test can
    /// reproduce the real service's ordering: planning is detached there, so a fast plan's
    /// <c>run-planned</c> can be delivered while the caller is still awaiting this reply.</summary>
    public Action? BeforeRunProfileReply { get; set; }

    public Task<Result<RunProfileResponse, IpcError>> RunProfileAsync(
        Guid profileId, string? path = null, Profile? draft = null, CancellationToken ct = default)
    {
        RunProfileCalls.Add((profileId, path));
        RunProfileDrafts.Add(draft);
        BeforeRunProfileReply?.Invoke();
        return Task.FromResult(
            path is not null && RunProfileResults.TryGetValue(path, out var scripted)
                ? scripted
                : RunProfileResult);
    }

    public List<Guid> RunPlanStreamCalls { get; } = [];

    /// <summary>The plan replayed to the caller's sink, keyed by run id, with
    /// <see cref="RunPlanResult"/> as the fallback for any other run. Scripted as a whole
    /// <c>DryRunReport</c> for the same reason <see cref="DryRunResult"/> is: it is the readable way to
    /// describe an expected row set, and the real service delivers the identical frames.</summary>
    public Dictionary<Guid, Result<DryRunReport, IpcError>> RunPlanResults { get; } = [];

    public Result<DryRunReport, IpcError> RunPlanResult { get; set; } =
        new DryRunReport
        {
            ProfileId = Guid.Empty,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = [],
            SourceFiles = [],
            DestinationFiles = [],
            SourceOperations = [],
            DestinationOperations = [],
        };

    /// <summary>When set, GetRunPlanStreamAsync awaits this before returning (supersede / cancellation
    /// tests, where a second preview must start while the first is still streaming).</summary>
    public TaskCompletionSource? RunPlanGate { get; set; }

    /// <summary>When set, GetRunPlanStreamAsync throws it (unexpected-exception tests).</summary>
    public Exception? RunPlanException { get; set; }

    /// <summary>Pages a paged store will be served, keyed by their first row. A request for a key that
    /// is not here comes back empty, which is what the real service does past the end of a view.</summary>
    public Dictionary<int, DryRunChunkResponse> Pages { get; } = [];

    /// <summary>Every page request, in order — so a test can assert what was fetched, and what was NOT
    /// (an evicted page must not be silently re-fetched during an assertion pass).</summary>
    public List<GetRunPlanPageRequest> PageRequests { get; } = [];

    /// <summary>What <see cref="GetRunPlanViewAsync"/> answers. Defaults to "no filter, no rows".</summary>
    public RunPlanViewResponse ViewResult { get; set; } = new() { ViewId = null, RowCount = 0 };

    public List<GetRunPlanViewRequest> ViewRequests { get; } = [];

    /// <summary>Run ids whose plan the preview OPENED, one entry per open.
    ///
    /// <para>Replaces the old <see cref="RunPlanStreamCalls"/> as the evidence that the rows on screen came
    /// from a particular RUN's snapshot rather than from a fresh scan. Recorded on the unfiltered
    /// source-side view request, which is what opening a plan issues exactly once; a later filter change
    /// carries a filter and does not count as an open.</para></summary>
    public List<Guid> RunPlanOpenCalls { get; } = [];

    /// <summary>When set, every page request awaits this before answering — the seam for asserting what
    /// a list shows while a page is still in flight, which is the placeholder path.</summary>
    public TaskCompletionSource? PageGate { get; set; }

    /// <summary>When set, <see cref="GetRunPlanViewAsync"/> answers with this instead of consulting the
    /// served plan — for the error and no-such-view paths.</summary>
    public Result<RunPlanViewResponse, IpcError>? ViewError { get; set; }

    /// <summary>Synthesizes a page on demand instead of looking one up.
    /// <para>For the plan sizes a fixture cannot afford to build: proving a preview's cost is flat
    /// between a 500,000-row plan and a five-million-row one means never materializing either, which is
    /// the same reason the preview itself does not.</para></summary>
    public Func<GetRunPlanPageRequest, DryRunChunkResponse>? PageFactory { get; set; }

    /// <summary>Runs whose whole plan is being served from a <see cref="FakePlanService"/>: header
    /// aggregates, filtered views and pages, all derived from one report. See <see cref="ServePlan"/>.</summary>
    private readonly Dictionary<Guid, FakePlanService> _plans = [];

    /// <summary>Puts a whole plan behind the three paged verbs, the way the service puts a run's frozen
    /// snapshot behind them. This is what a view-model test wants: it describes the plan, and the tab
    /// reads it through the same calls the app makes.
    ///
    /// <para>Also scripts <c>get-run-detail</c>, because the preview opens by reading the header — the
    /// facet keys and status totals a windowed client cannot count for itself.</para></summary>
    public FakePlanService ServePlan(Guid runId, DryRunReport plan, Profile? profile = null)
    {
        FakePlanService service = new(plan);
        _plans[runId] = service;
        RunDetailResults[runId] = service.Detail(runId, profile ?? TestData.ProfileFactory.Sample());
        return service;
    }

    /// <summary>The plan any run gets when nothing was served for it specifically, derived from
    /// <see cref="DryRunResult"/>.
    ///
    /// <para>Needed because the shell's own tests never learn the run id until the engine event arrives —
    /// they script "this is the plan" and let <c>MainWindowViewModel</c> route it. Built once per report
    /// so repeated calls for the same run answer from the same order and view table.</para></summary>
    private FakePlanService? Fallback(Guid runId)
    {
        if (_plans.TryGetValue(runId, out FakePlanService? served))
            return served;
        if (!DryRunResultSet || !DryRunResult.TryGetValue(out DryRunReport? plan))
            return null;
        if (!ReferenceEquals(plan, _fallbackFor))
        {
            _fallbackFor = plan;
            _fallback = new FakePlanService(plan);
        }
        return _fallback;
    }

    private DryRunReport? _fallbackFor;
    private FakePlanService? _fallback;

    /// <summary>When set, every view request awaits this before answering — the seam for asserting what
    /// happens WHILE a filter is being built service-side (the loading overlay, and a load that
    /// supersedes a rebuild still in flight).</summary>
    public TaskCompletionSource? ViewGate { get; set; }

    public async Task<Result<RunPlanViewResponse, IpcError>> GetRunPlanViewAsync(
        GetRunPlanViewRequest request, CancellationToken ct = default)
    {
        ViewRequests.Add(request);
        if (request.Side == RunPlanSide.Sources
            && request.Search is null && request.SourceRoots is null && request.DestinationRoots is null
            && request.Kinds is null && !request.IncludeDestructiveDisposition)
        {
            RunPlanOpenCalls.Add(request.RunId);
        }
        if (ViewGate is not null)
        {
            try
            {
                await ViewGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<RunPlanViewResponse, IpcError>.Canceled();
            }
        }
        if (ViewError is { } scripted)
            return scripted;
        return Result<RunPlanViewResponse, IpcError>.Success(
            Pages.Count == 0 && Fallback(request.RunId) is { } plan ? plan.View(request) : ViewResult);
    }

    public async Task<Result<DryRunChunkResponse, IpcError>> GetRunPlanPageAsync(
        GetRunPlanPageRequest request, CancellationToken ct = default)
    {
        PageRequests.Add(request);
        if (PageGate is not null)
        {
            try
            {
                await PageGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<DryRunChunkResponse, IpcError>.Canceled();
            }
        }
        if (PageFactory is { } factory)
            return Result<DryRunChunkResponse, IpcError>.Success(factory(request));
        if (Pages.Count == 0 && Fallback(request.RunId) is { } plan)
            return Result<DryRunChunkResponse, IpcError>.Success(plan.Page(request));
        return Pages.TryGetValue(request.First, out DryRunChunkResponse? page)
            ? Result<DryRunChunkResponse, IpcError>.Success(page)
            : Result<DryRunChunkResponse, IpcError>.Success(DryRunColumns.ToChunk([], [], [], [], []));
    }

    public async Task<Result<DryRunCompletion, IpcError>> GetRunPlanStreamAsync(
        Guid runId, IDryRunChunkSink sink, CancellationToken ct = default)
    {
        RunPlanStreamCalls.Add(runId);
        if (RunPlanException is not null)
            throw RunPlanException;
        if (RunPlanGate is not null)
        {
            try
            {
                await RunPlanGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<DryRunCompletion, IpcError>.Canceled();
            }
        }

        Result<DryRunReport, IpcError> scripted =
            RunPlanResults.TryGetValue(runId, out var forRun) ? forRun : RunPlanResult;
        if (scripted.IsCanceled)
            return Result<DryRunCompletion, IpcError>.Canceled();
        if (scripted.TryGetError(out IpcError? planError))
            return planError;
        scripted.TryGetValue(out DryRunReport? plan);
        sink.OnChunk(DryRunColumns.ToChunk(
            plan!.Directories, plan.SourceFiles, plan.DestinationFiles,
            plan.SourceOperations, plan.DestinationOperations));
        return new DryRunCompletion(plan.GeneratedAt, plan.Truncated, plan.Space);
    }

    public List<(Guid RunId, bool Approve)> ApproveRunCalls { get; } = [];

    /// <summary>The acknowledgment flag each approval carried, so a test can assert the footer's
    /// checkbox actually reaches the engine — without it the gate is unobservable from the client side.</summary>
    public List<bool> ApproveRunAcknowledgements { get; } = [];
    public Result<bool, IpcError> ApproveRunResult { get; set; } = true;

    public Task<Result<bool, IpcError>> ApproveRunAsync(
        Guid runId, bool approve, bool acknowledgeWarnings = false, CancellationToken ct = default)
    {
        ApproveRunCalls.Add((runId, approve));
        ApproveRunAcknowledgements.Add(acknowledgeWarnings);
        return Task.FromResult(ApproveRunResult);
    }

    public List<Guid> CancelRunCalls { get; } = [];
    public Result<bool, IpcError> CancelRunResult { get; set; } = true;

    public Task<Result<bool, IpcError>> CancelRunAsync(Guid runId, CancellationToken ct = default)
    {
        CancelRunCalls.Add(runId);
        return Task.FromResult(CancelRunResult);
    }

    public List<Guid> DiscardRunCalls { get; } = [];
    public Result<bool, IpcError> DiscardRunResult { get; set; } = true;

    public Task<Result<bool, IpcError>> DiscardRunAsync(Guid runId, CancellationToken ct = default)
    {
        DiscardRunCalls.Add(runId);
        return Task.FromResult(DiscardRunResult);
    }

    public List<(Guid RunId, bool Paused)> SetRunPausedCalls { get; } = [];
    public Result<bool, IpcError> SetRunPausedResult { get; set; } = true;

    public Task<Result<bool, IpcError>> SetRunPausedAsync(
        Guid runId, bool paused, CancellationToken ct = default)
    {
        SetRunPausedCalls.Add((runId, paused));
        return Task.FromResult(SetRunPausedResult);
    }

    public int GetRunsCalls { get; private set; }
    public Result<IReadOnlyList<RunSummaryDto>, IpcError> RunsResult { get; set; } =
        Result<IReadOnlyList<RunSummaryDto>, IpcError>.Success([]);

    public Task<Result<IReadOnlyList<RunSummaryDto>, IpcError>> GetRunsAsync(CancellationToken ct = default)
    {
        GetRunsCalls++;
        return Task.FromResult(RunsResult);
    }

    public List<Guid> GetRunDetailCalls { get; } = [];

    /// <summary>Per-run scripted answers, falling back to <see cref="RunDetailResult"/>.</summary>
    public Dictionary<Guid, Result<RunDetailDto, IpcError>> RunDetailResults { get; } = [];

    /// <summary>Defaults to the answer a run still PLANNING really gets, so a test that does not care
    /// about the summary pane is not made to build a whole Profile to stay quiet.</summary>
    /// <summary>An explicit override. Left unset, run detail is derived from whatever plan is being
    /// served (see <see cref="Fallback"/>); setting it — including to the "still planning" answer below —
    /// takes precedence, which is what the job-queue tests want.</summary>
    public Result<RunDetailDto, IpcError> RunDetailResult
    {
        get => _runDetailResult;
        set { _runDetailResult = value; _runDetailResultSet = true; }
    }

    private Result<RunDetailDto, IpcError> _runDetailResult =
        new IpcError("RUN_PLAN_UNAVAILABLE", "the run has no plan snapshot yet");
    private bool _runDetailResultSet;

    /// <summary>Held per run id, so a test can leave one fetch in flight and prove the next selection
    /// cancels it — the same recipe <see cref="JobLogGates"/> uses.</summary>
    public Dictionary<Guid, TaskCompletionSource> RunDetailGates { get; } = [];

    public async Task<Result<RunDetailDto, IpcError>> GetRunDetailAsync(
        Guid runId, CancellationToken ct = default)
    {
        GetRunDetailCalls.Add(runId);
        // The preview's FIRST call is this one, so the plan-level gate and exception belong here too: a
        // test holding a preview mid-open, or making one fault, scripts them and means this.
        if (RunPlanException is not null)
            throw RunPlanException;
        if (RunPlanGate is not null)
        {
            try
            {
                await RunPlanGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<RunDetailDto, IpcError>.Canceled();
            }
        }
        if (RunDetailGates.TryGetValue(runId, out TaskCompletionSource? gate))
        {
            try
            {
                await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<RunDetailDto, IpcError>.Canceled();   // mirror the real gateway
            }
        }
        if (RunDetailResults.TryGetValue(runId, out var scripted))
            return scripted;
        if (_runDetailResultSet)
            return RunDetailResult;
        // A refused plan is refused here now: the preview opens by reading the header, so an error
        // scripted onto DryRunResult has to surface from this call rather than from a stream.
        if (DryRunResultSet && DryRunResult.TryGetError(out IpcError? planError))
            return planError;
        if (Fallback(runId) is { } plan)
            return plan.Detail(runId, GetResult.TryGetValue(out Profile? profile) ? profile : TestData.ProfileFactory.Sample());
        return RunDetailResult;
    }

    public async Task<Result<bool, IpcError>> SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        SetPausedCalls.Add(paused);
        if (SetPausedGate is not null)
        {
            try
            {
                await SetPausedGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<bool, IpcError>.Canceled();   // mirror the real gateway
            }
        }
        return SetPausedResult;
    }

    public Task<Result<IReadOnlyList<JobSummaryDto>, IpcError>> GetRecentJobsAsync(
        int count = 50, CancellationToken ct = default)
    {
        RecentJobsCalls.Add(count);
        return Task.FromResult(RecentJobsResult);
    }

    public async Task<Result<IReadOnlyList<string>, IpcError>> GetJobLogAsync(
        Guid jobId, CancellationToken ct = default)
    {
        JobLogCalls.Add(jobId);
        if (JobLogGates.TryGetValue(jobId, out TaskCompletionSource? gate))
        {
            try
            {
                await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return Result<IReadOnlyList<string>, IpcError>.Canceled();
            }
        }
        return JobLogResults.TryGetValue(jobId, out var scripted) ? scripted : JobLogResult;
    }

    public async IAsyncEnumerable<Result<EngineEvent, IpcError>> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SubscribeCalls++;
        if (SubscribeSegments.Count > 0)
        {
            foreach (Result<EngineEvent, IpcError> item in SubscribeSegments.Dequeue())
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
            yield break;
        }

        // Exhausted: park so the pump under test doesn't spin reconnecting.
        Task park = SubscribeIdleGate?.Task ?? Task.Delay(Timeout.Infinite, ct);
        try
        {
            await park.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
    }
}

internal sealed class FakeFolderPicker(string? result = null) : IFolderPicker
{
    /// <summary>Files returned by <see cref="PickFilesAsync"/> (empty = user cancelled).</summary>
    public IReadOnlyList<string> FilesResult { get; set; } = [];

    /// <summary>The start folder the caller asked for on the last <see cref="PickFolderAsync"/> call —
    /// the assertion seam for the "open where the user already is" behaviour.</summary>
    public string? LastStartNear { get; private set; }

    public Task<string?> PickFolderAsync(string title, string? startNear = null)
    {
        LastStartNear = startNear;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult(FilesResult);

    /// <summary>Path returned by <see cref="PickFileAsync"/> (null = user cancelled). Separate from
    /// the ctor's <c>result</c> so a test can drive the folder and file pickers independently.</summary>
    public string? FileResult { get; set; }

    /// <summary>The start file and patterns asked for on the last <see cref="PickFileAsync"/> call.</summary>
    public string? LastFileStartNear { get; private set; }
    public IReadOnlyList<string> LastFilePatterns { get; private set; } = [];

    public Task<string?> PickFileAsync(
        string title, string filterName, IReadOnlyList<string> patterns, string? startNear = null)
    {
        LastFileStartNear = startNear;
        LastFilePatterns = patterns;
        return Task.FromResult(FileResult);
    }
}
