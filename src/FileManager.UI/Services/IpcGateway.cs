using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Connect-on-demand gateway over one shared IpcClient. The service is auto-started
/// via ServiceLauncher (§3.3). A transport fault disposes the client so the next call
/// reconnects — no background retry loop. Dry-run deliberately uses its OWN short-lived
/// connection: requests on a connection are strictly sequential (§3.2), so a cancelled or
/// minutes-long dry-run must not desynchronize or stall the main channel's status polls.
/// <para>The event subscription likewise takes its own connection — mandatory, since subscribing
/// makes a connection one-way — and is likewise single-attempt: <see cref="EngineEventPump"/> owns
/// the reconnect loop, keeping this class free of background retry.</para></summary>
/// <param name="serviceExePath">Supplies the user-configured service executable path. A delegate, not
/// a string: this gateway is built once at startup and lives for the whole session, so a snapshot
/// would go stale the moment the user corrects a bad path. It is invoked only on a connect attempt —
/// i.e. only while disconnected — where a small file read is invisible next to the launcher's ~5 s
/// retry budget, and that is what lets a corrected path take effect with no app restart. Null (the
/// default) means "no configured path", leaving the launcher's original resolution.</param>
/// <param name="time">Clock for the start-attempt cooldown. Injectable so the cooldown is testable
/// without waiting a real minute.</param>
public sealed class IpcGateway(Func<string?>? serviceExePath = null, TimeProvider? time = null)
    : IIpcGateway, IAsyncDisposable
{
    /// <summary>How long a failed start attempt suppresses further start attempts for the SAME
    /// executable. Long enough that a broken exe is launched at most once a minute instead of on
    /// every 2 s status poll; short enough that a service which crashed once recovers on its own.</summary>
    private static readonly TimeSpan StartCooldown = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Func<string?>? _serviceExePath = serviceExePath;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private IpcClient? _client;

    /// <summary>The executable whose last start attempt failed, and when. Guarded by
    /// <see cref="_connectGate"/> — the only place either is touched.
    /// <para>Without this, an executable that starts but never serves (the classic case: the setting
    /// points at some other program) is re-launched on every poll, forever, each attempt holding the
    /// connect gate for the launcher's full ~5 s retry budget. That starves every other request —
    /// including the settings load — so the window the user needs in order to FIX the path never
    /// opens. Keyed by path so correcting the setting retries immediately.</para></summary>
    private string? _failedStartPath;
    private long _failedStartAt;

    public Task<Result<EngineStatusSnapshot, IpcError>> GetStatusAsync(CancellationToken ct = default) =>
        RequestAsync<StatusResponse, EngineStatusSnapshot>(
            new GetStatusRequest(), static r => r.Status, ct);

    public Task<Result<IReadOnlyList<ProfileSummary>, IpcError>> ListProfilesAsync(CancellationToken ct = default) =>
        RequestAsync<ProfileListResponse, IReadOnlyList<ProfileSummary>>(
            new ListProfilesRequest(), static r => r.Profiles, ct);

    public Task<Result<Profile, IpcError>> GetProfileAsync(Guid profileId, CancellationToken ct = default) =>
        RequestAsync<ProfileResponse, Profile>(
            new GetProfileRequest { ProfileId = profileId }, static r => r.Profile, ct);

    public async Task<Result<SaveOutcome, IpcError>> SaveProfileAsync(
        Profile profile, bool acknowledgeWarnings, CancellationToken ct = default)
    {
        var response = await RequestAsync<ValidationResponse, IReadOnlyList<ValidationIssue>>(
            new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = acknowledgeWarnings },
            static r => r.Issues, ct).ConfigureAwait(false);
        if (response.TryGetError(out IpcError? error))
            return error;
        response.TryGetValue(out IReadOnlyList<ValidationIssue>? issues);

        // Mirror of IProfileStore.Save's blocking rule — the wire carries issues, not a flag.
        bool saved = issues!.All(i => i.Severity != ValidationSeverity.Error)
            && (acknowledgeWarnings || issues!.All(i => i.Severity != ValidationSeverity.BlockingWarning));
        return new SaveOutcome(saved, issues!);
    }

    public Task<Result<bool, IpcError>> DeleteProfileAsync(Guid profileId, CancellationToken ct = default) =>
        RequestAsync<OkResponse, bool>(
            new DeleteProfileRequest { ProfileId = profileId }, static _ => true, ct);

    public Task<Result<GlobalSettings, IpcError>> GetSettingsAsync(CancellationToken ct = default) =>
        RequestAsync<SettingsResponse, GlobalSettings>(
            new GetSettingsRequest(), static r => r.Settings, ct);

    public Task<Result<GlobalSettings, IpcError>> SaveSettingsAsync(GlobalSettings settings, CancellationToken ct = default) =>
        RequestAsync<SettingsResponse, GlobalSettings>(
            new UpdateSettingsRequest { Settings = settings }, static r => r.Settings, ct);

    public Task<Result<RelocateProfilesResponse, IpcError>> RelocateProfilesAsync(
        string newDirectory, bool moveExisting, CancellationToken ct = default) =>
        RequestAsync<RelocateProfilesResponse, RelocateProfilesResponse>(
            new RelocateProfilesRequest { NewDirectory = newDirectory, MoveExisting = moveExisting },
            static r => r, ct);

    public Task<Result<bool, IpcError>> ShutdownServiceAsync(CancellationToken ct = default) =>
        RequestAsync<OkResponse, bool>(
            new ShutdownRequest(), static _ => true, ct);

    public Task<Result<RunProfileResponse, IpcError>> RunProfileAsync(
        Guid profileId, string? path = null, Profile? draft = null, CancellationToken ct = default) =>
        RequestAsync<RunProfileResponse, RunProfileResponse>(
            new RunProfileRequest { ProfileId = profileId, Path = path, InlineProfile = draft },
            static r => r, ct);

    public Task<Result<bool, IpcError>> ApproveRunAsync(
        Guid runId, bool approve, bool acknowledgeWarnings = false, CancellationToken ct = default) =>
        RequestAsync<OkResponse, bool>(
            new ApproveRunRequest
            {
                RunId = runId,
                Approve = approve,
                AcknowledgeWarnings = acknowledgeWarnings,
            },
            static _ => true, ct);

    public Task<Result<bool, IpcError>> CancelRunAsync(Guid runId, CancellationToken ct = default) =>
        RequestAsync<OkResponse, bool>(
            new CancelRunRequest { RunId = runId }, static _ => true, ct);

    public Task<Result<bool, IpcError>> SetPausedAsync(bool paused, CancellationToken ct = default) =>
        RequestAsync<OkResponse, bool>(
            new SetPausedRequest { Paused = paused }, static _ => true, ct);

    public Task<Result<IReadOnlyList<JobSummaryDto>, IpcError>> GetRecentJobsAsync(
        int count = 50, CancellationToken ct = default) =>
        RequestAsync<RecentJobsResponse, IReadOnlyList<JobSummaryDto>>(
            new GetRecentJobsRequest { Count = count }, static r => r.Jobs, ct);

    public Task<Result<IReadOnlyList<string>, IpcError>> GetJobLogAsync(
        Guid jobId, CancellationToken ct = default) =>
        RequestAsync<JobLogResponse, IReadOnlyList<string>>(
            new GetJobLogRequest { JobId = jobId }, static r => r.Lines, ct);

    public async IAsyncEnumerable<Result<EngineEvent, IpcError>> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Own connection: after SubscribeAsync the connection is a one-way event stream and must never
        // carry a request (§3.2), so it can never be the shared _client.
        //
        // ConnectAsync, deliberately NOT ServiceLauncher.ConnectOrStartAsync: the pump retries this on
        // a backoff, and ConnectOrStartAsync would spawn a FileManager.Service process (and burn a ~5s
        // retry budget) on every attempt during a real outage. The 2s status poll already owns
        // service-start duty through the shared client.
        var connected = await IpcClient.ConnectAsync(ct).ConfigureAwait(false);
        if (connected.IsCanceled)
            yield break;
        if (connected.TryGetError(out string? connectError))
        {
            yield return new IpcError("SERVICE_UNAVAILABLE", connectError);
            yield break;
        }
        connected.TryGetValue(out IpcClient? client);

        await using (client)   // `using` lowers to try/finally, which permits `yield return`
        {
            // Manual enumeration: `yield return` is illegal inside a try that has a catch, so the
            // try/catch wraps ONLY MoveNextAsync — which is where SubscribeAsync's throws originate.
            await using IAsyncEnumerator<EngineEvent> events =
                client!.SubscribeAsync(ct).GetAsyncEnumerator(ct);
            while (true)
            {
                EngineEvent? next;
                IpcError? failure = null;
                try
                {
                    if (!await events.MoveNextAsync().ConfigureAwait(false))
                        break;                      // clean close at a frame boundary
                    next = events.Current;
                }
                catch (OperationCanceledException)
                {
                    break;                          // shutting down — not a failure
                }
                catch (InvalidOperationException ex)
                {
                    failure = new IpcError("EVENTS_REFUSED", ex.Message);
                    next = null;
                }
                catch (IOException ex)
                {
                    failure = new IpcError("IPC_TRANSPORT", ex.Message);
                    next = null;
                }
                catch (Exception ex)
                {
                    // Last-resort catch-all (directive): the stream must surface a value, never throw
                    // into the consumer's `await foreach`.
                    Log.Error(ex, "Engine event subscription failed unexpectedly");
                    failure = new IpcError("IPC_INTERNAL", $"{ex.GetType().Name}: {ex.Message}");
                    next = null;
                }

                if (failure is not null)
                {
                    yield return failure;
                    yield break;                    // one Failure item, then the sequence ends
                }
                yield return next!;
            }
        }
    }

    public async Task<Result<DryRunCompletion, IpcError>> GetRunPlanStreamAsync(
        Guid runId, IDryRunChunkSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);

        // Own connection: a cancelled stream leaves unread frames on the pipe, so it must be discardable
        // without desyncing the shared channel. May start the service — this path runs once per explicit
        // user action, so it cannot become the runaway loop the shared channel's 2 s poll can.
        var connected = await ServiceLauncher
            .ConnectOrStartAsync(_serviceExePath?.Invoke(), allowStart: true, ct).ConfigureAwait(false);
        if (connected.IsCanceled)
            return Result<DryRunCompletion, IpcError>.Canceled();
        if (connected.TryGetError(out string? connectError))
        {
            Log.Warning("Run-plan stream could not connect to the service: {Error}", connectError);
            return new IpcError("SERVICE_UNAVAILABLE", connectError);
        }
        connected.TryGetValue(out IpcClient? client);

        await using (client)
        {
            var response = await client!.RunPlanStreamAsync(
                new GetRunPlanStreamRequest { RunId = runId }, sink, ct).ConfigureAwait(false);
            if (response.IsCanceled)
                return Result<DryRunCompletion, IpcError>.Canceled();
            if (response.TryGetError(out IpcError? error))
            {
                Log.Warning("Run-plan stream for run {RunId} failed: {Code} {Message}",
                    runId, error.Code, error.Message);
                return error;
            }
            response.TryGetValue(out DryRunCompletion? completion);
            return completion!;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IpcClient? client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
        _connectGate.Dispose();
    }

    private async Task<Result<TValue, IpcError>> RequestAsync<TResponse, TValue>(
        IpcRequest request, Func<TResponse, TValue> project, CancellationToken ct)
        where TResponse : IpcResponse
    {
        try
        {
            var clientResult = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            if (clientResult.IsCanceled)
                return Result<TValue, IpcError>.Canceled();
            if (clientResult.TryGetError(out IpcError? connectError))
            {
                if (request is not GetStatusRequest)
                    Log.Warning("IPC request {RequestType} could not connect: {Code} {Message}",
                        request.GetType().Name, connectError.Code, connectError.Message);
                return connectError;
            }
            clientResult.TryGetValue(out IpcClient? client);

            var response = await client!.RequestAsync<TResponse>(request, ct).ConfigureAwait(false);

            // A cancelled request may have desynchronized the connection (the client marks itself
            // poisoned once its write began) — recycle it so the NEXT request gets a clean one
            // instead of reading this request's leftover response.
            if (client.IsPoisoned)
                await DropClientAsync(client).ConfigureAwait(false);

            if (response.IsCanceled)
                return Result<TValue, IpcError>.Canceled();
            if (response.TryGetError(out IpcError? error))
            {
                // The status poll fires every couple of seconds; logging each failed poll here would
                // flood the log during an outage. StatusBarViewModel logs that once, on transition.
                if (request is not GetStatusRequest)
                    Log.Warning("IPC request {RequestType} failed: {Code} {Message}",
                        request.GetType().Name, error.Code, error.Message);
                if (error.Code is "IPC_TRANSPORT" or "IPC_UNEXPECTED_RESPONSE" or "IPC_MALFORMED"
                    && !client.IsPoisoned)   // poisoned was already dropped above
                    await DropClientAsync(client).ConfigureAwait(false);
                return error;
            }
            response.TryGetValue(out TResponse? typed);
            return project(typed!);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): nothing may escape raw into viewmodel code —
            // several call sites (commands, timers) have no exception boundary of their own.
            Log.Error(ex, "IPC request {RequestType} failed unexpectedly", request.GetType().Name);
            return new IpcError("IPC_INTERNAL", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<Result<IpcClient, IpcError>> EnsureConnectedAsync(CancellationToken ct)
    {
        try
        {
            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the gate was acquired — nothing to release.
            return Result<IpcClient, IpcError>.Canceled();
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): DisposeAsync disposes _connectGate; a request
            // racing shutdown must get a failure value, not an unhandled ObjectDisposedException.
            Log.Warning(ex, "IPC connect gate unavailable (gateway shutting down?)");
            return new IpcError("IPC_TRANSPORT", $"gateway is shutting down: {ex.GetType().Name}");
        }
        try
        {
            if (_client is not null)
                return _client;

            string? exePath = _serviceExePath?.Invoke();
            bool allowStart = MayStart(exePath);
            var connected = await ServiceLauncher
                .ConnectOrStartAsync(exePath, allowStart, ct).ConfigureAwait(false);
            if (connected.IsCanceled)
                return Result<IpcClient, IpcError>.Canceled();
            if (connected.TryGetError(out string? error))
            {
                if (allowStart)
                    RecordFailedStart(exePath);
                return new IpcError("SERVICE_UNAVAILABLE", error);
            }
            connected.TryGetValue(out IpcClient? client);
            _client = client;
            // A connection proves whatever is configured works, so the next outage starts clean.
            _failedStartPath = null;
            return client!;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Whether this attempt may spawn a process. False only while the SAME executable's last
    /// start attempt failed less than <see cref="StartCooldown"/> ago — a different (or corrected)
    /// path is always allowed to try immediately. Callers hold <see cref="_connectGate"/>.
    /// <para>Internal purely as a test seam: the policy is what stops the runaway respawn, but the
    /// path that exercises it opens real named pipes.</para></summary>
    internal bool MayStart(string? exePath)
    {
        if (_failedStartPath is null)
            return true;
        if (!string.Equals(_failedStartPath, exePath ?? "", StringComparison.OrdinalIgnoreCase))
            return true;
        return _time.GetElapsedTime(_failedStartAt) >= StartCooldown;
    }

    internal void RecordFailedStart(string? exePath)
    {
        _failedStartPath = exePath ?? "";
        _failedStartAt = _time.GetTimestamp();
    }

    /// <summary>Forgets the current connection so the next request reconnects from scratch.
    /// <para>Used when the service executable path changes: the cached client points at whatever was
    /// running before, so without this the UI would keep talking to the old service and the new
    /// setting would look like it did nothing.</para></summary>
    /// <param name="allowStart">True clears the start cooldown, because the caller is acting on a
    /// deliberate change rather than retrying a failure, so the next request may spawn the configured
    /// executable. False ARMS the cooldown instead, which is what a caller resetting in a loop needs:
    /// clearing it every round is what would turn one reconnect poll into one process launch per round,
    /// each carrying the launcher's full retry budget.</param>
    public async Task ResetConnectionAsync(bool allowStart = true)
    {
        IpcClient? stale;
        try
        {
            await _connectGate.WaitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Shutting down; there is nothing left to reset.
            Log.Warning(ex, "IPC connect gate unavailable while resetting the connection");
            return;
        }
        try
        {
            stale = _client;
            _client = null;
            if (allowStart)
                _failedStartPath = null;
            else
                RecordFailedStart(_serviceExePath?.Invoke());
        }
        finally
        {
            _connectGate.Release();
        }

        if (stale is not null)
            await DisposeQuietlyAsync(stale).ConfigureAwait(false);
    }

    private static async Task DisposeQuietlyAsync(IpcClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): teardown must never replace a caller's real result.
            Log.Warning(ex, "Disposing a replaced IPC client failed");
        }
    }

    private async Task DropClientAsync(IpcClient client)
    {
        try
        {
            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_client, client))
                    _client = null;
            }
            finally
            {
                _connectGate.Release();
            }
            // Disposing while another request is mid-flight on this client is a benign race: the
            // client tolerates it (its request gate survives disposal) and the loser gets a
            // transport-failure value.
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): dropping a dead client is best-effort cleanup and
            // must never replace the caller's real result with a teardown exception.
            Log.Warning(ex, "Dropping a dead IPC client failed");
        }
    }
}
