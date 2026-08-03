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
    public List<Guid> DryRunCalls { get; } = [];
    /// <summary>The inline draft passed to each DryRunAsync call (null when the persisted-profile
    /// path was used), recorded in lockstep with <see cref="DryRunCalls"/>.</summary>
    public List<Profile?> DryRunDrafts { get; } = [];
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

    public Result<DryRunReport, IpcError> DryRunResult { get; set; } =
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

    /// <summary>When set, DryRunAsync awaits this before returning (cancellation tests).</summary>
    public TaskCompletionSource? DryRunGate { get; set; }

    /// <summary>When set, DryRunAsync throws it (unexpected-exception tests).</summary>
    public Exception? DryRunException { get; set; }

    /// <summary>When set, DryRunAsync reports these to the caller's IProgress (synchronously, in
    /// order) before returning — progress-caption tests.</summary>
    public IReadOnlyList<DryRunProgress>? ScriptedProgress { get; set; }

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

    /// <summary>Replays <see cref="DryRunResult"/> to the caller's sink as a single chunk — the same
    /// shape the real gateway delivers, just in one frame instead of many — and returns its run-level
    /// facts. Tests keep scripting a whole <c>DryRunReport</c>, which stays the convenient way to
    /// describe an expected run; only the delivery mechanism changed.</summary>
    public async Task<Result<DryRunCompletion, IpcError>> DryRunAsync(
        Guid profileId, IDryRunChunkSink sink, IProgress<DryRunProgress>? progress = null,
        Profile? draft = null, CancellationToken ct = default)
    {
        DryRunCalls.Add(profileId);
        DryRunDrafts.Add(draft);
        if (DryRunException is not null)
            throw DryRunException;
        if (ScriptedProgress is not null && progress is not null)
            foreach (DryRunProgress update in ScriptedProgress)
                progress.Report(update);
        if (DryRunGate is not null)
        {
            try
            {
                await DryRunGate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Mirror the real gateway: cancellation is a Canceled result, never a throw.
                return Result<DryRunCompletion, IpcError>.Canceled();
            }
        }

        if (DryRunResult.IsCanceled)
            return Result<DryRunCompletion, IpcError>.Canceled();
        if (DryRunResult.TryGetError(out IpcError? error))
            return error;
        DryRunResult.TryGetValue(out DryRunReport? report);
        sink.OnChunk(DryRunColumns.ToChunk(
            report!.Directories, report.SourceFiles, report.DestinationFiles,
            report.SourceOperations, report.DestinationOperations));
        return new DryRunCompletion(report.GeneratedAt, report.Truncated, report.Space);
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

    public List<(Guid ProfileId, string Path)> RunProfileCalls { get; } = [];

    public Result<RunProfileResponse, IpcError> RunProfileResult { get; set; } =
        new RunProfileResponse { QueuedCount = 1, Scanning = false, RunId = Guid.NewGuid() };

    /// <summary>Per-path override, so one test can script an accepted root and a failing one.</summary>
    public Dictionary<string, Result<RunProfileResponse, IpcError>> RunProfileResults { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<bool> SetPausedCalls { get; } = [];
    public Result<bool, IpcError> SetPausedResult { get; set; } = true;

    /// <summary>When set, SetPausedAsync awaits this before returning (poll-race tests). Mirrors
    /// <see cref="DryRunGate"/>.</summary>
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

    public Task<Result<RunProfileResponse, IpcError>> RunProfileAsync(
        Guid profileId, string path, CancellationToken ct = default)
    {
        RunProfileCalls.Add((profileId, path));
        return Task.FromResult(
            RunProfileResults.TryGetValue(path, out var scripted) ? scripted : RunProfileResult);
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
