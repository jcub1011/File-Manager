using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Connect-on-demand gateway over one shared IpcClient. The service is auto-started
/// via ServiceLauncher (§3.3). A transport fault disposes the client so the next call
/// reconnects — no background retry loop. Dry-run deliberately uses its OWN short-lived
/// connection: requests on a connection are strictly sequential (§3.2), so a cancelled or
/// minutes-long dry-run must not desynchronize or stall the main channel's status polls.</summary>
public sealed class IpcGateway : IIpcGateway, IAsyncDisposable
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private IpcClient? _client;

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

    public async Task<Result<DryRunReport, IpcError>> DryRunAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default)
    {
        // Own connection: cancel = dispose, leaving the shared channel clean.
        var connected = await ServiceLauncher.ConnectOrStartAsync(ct).ConfigureAwait(false);
        if (connected.IsCanceled)
            return Result<DryRunReport, IpcError>.Canceled();
        if (connected.TryGetError(out string? connectError))
        {
            Log.Warning("Dry-run could not connect to the service: {Error}", connectError);
            return new IpcError("SERVICE_UNAVAILABLE", connectError);
        }
        connected.TryGetValue(out IpcClient? client);

        await using (client)
        {
            // Streamed: the report arrives as many small frames and is reassembled here, so it is
            // not bounded by the single-frame size cap (no ~50k-file truncation).
            var response = await client!.DryRunStreamAsync(
                new DryRunStreamRequest { ProfileId = profileId, ScopePath = scopePath }, ct).ConfigureAwait(false);
            if (response.IsCanceled)
                return Result<DryRunReport, IpcError>.Canceled();
            if (response.TryGetError(out IpcError? error))
            {
                Log.Warning("Dry-run IPC request for profile {ProfileId} failed: {Code} {Message}",
                    profileId, error.Code, error.Message);
                return error;
            }
            response.TryGetValue(out DryRunReport? report);
            return report!;
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
        if (response.IsCanceled)
            return Result<TValue, IpcError>.Canceled();
        if (response.TryGetError(out IpcError? error))
        {
            // The status poll fires every couple of seconds; logging each failed poll here would
            // flood the log during an outage. StatusBarViewModel logs that once, on transition.
            if (request is not GetStatusRequest)
                Log.Warning("IPC request {RequestType} failed: {Code} {Message}",
                    request.GetType().Name, error.Code, error.Message);
            if (error.Code == "IPC_TRANSPORT")
                await DropClientAsync(client).ConfigureAwait(false);
            return error;
        }
        response.TryGetValue(out TResponse? typed);
        return project(typed!);
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
        try
        {
            if (_client is not null)
                return _client;
            var connected = await ServiceLauncher.ConnectOrStartAsync(ct).ConfigureAwait(false);
            if (connected.IsCanceled)
                return Result<IpcClient, IpcError>.Canceled();
            if (connected.TryGetError(out string? error))
                return new IpcError("SERVICE_UNAVAILABLE", error);
            connected.TryGetValue(out IpcClient? client);
            _client = client;
            return client!;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task DropClientAsync(IpcClient client)
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
        await client.DisposeAsync().ConfigureAwait(false);
    }
}
