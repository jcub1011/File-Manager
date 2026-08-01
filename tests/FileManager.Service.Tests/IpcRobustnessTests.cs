using FileManager.Contracts.IPC;
using FileManager.Core.IPC;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace FileManager.Service.Tests;

/// <summary>Failure-path robustness over a real named pipe: a cancelled request must poison its
/// connection (never desync-read the next response), disposal must race safely with an in-flight
/// request, a hostile frame must not take the server down, and a pipe-name squatter must surface
/// as a retryable accept failure instead of killing the accept loop.</summary>
[SupportedOSPlatform("windows")]
[Collection(PipeCollection.Name)]
public sealed class IpcRobustnessTests : IAsyncLifetime
{
    private IpcServer? _server;

    /// <summary>Responds after a delay so a test can cancel the client side mid-request.</summary>
    private sealed class SlowStatusHandler : IIpcRequestHandler
    {
        public string RequestType => IpcRequestTypes.GetStatus;

        public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            return new StatusResponse { Status = new EngineStatusSnapshot(false, 0, 0, 0, null) };
        }
    }

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", "fm-robust-" + Guid.NewGuid().ToString("N"));
        IIpcRequestHandler[] handlers = [new SlowStatusHandler()];
        _server = new IpcServer(
            NullLogger<IpcServer>.Instance,
            new WindowsIpcEndpointProvider(NullLogger<WindowsIpcEndpointProvider>.Instance),
            handlers.ToDictionary(h => h.RequestType));
        Assert.True(_server.Start().IsSuccess);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", null);
    }

    [Fact]
    public async Task Cancelled_request_poisons_the_connection_and_a_fresh_one_recovers()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            // Cancel after the request frame is on the wire but before the (slow) response arrives:
            // the response stays queued on the pipe, so this connection can never be trusted again.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var cancelled = await client!.RequestAsync<StatusResponse>(new GetStatusRequest(), cts.Token);
            Assert.True(cancelled.IsCanceled);
            Assert.True(client.IsPoisoned);

            // The poisoned client refuses further use with a failure value — it must NOT read the
            // stale response of the cancelled request as if it answered this one.
            var refused = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(refused.TryGetError(out IpcError? error));
            Assert.Equal("IPC_TRANSPORT", error!.Code);
        }

        // A fresh connection is fully functional.
        var reconnected = await IpcClient.ConnectAsync();
        Assert.True(reconnected.TryGetValue(out IpcClient? fresh));
        await using (fresh)
        {
            var ok = await fresh!.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(ok.TryGetValue(out _));
        }
    }

    /// <summary>An unregistered request type answers NOT_IMPLEMENTED rather than dropping the
    /// connection or hanging the caller. This fixture registers exactly one handler, which makes it the
    /// natural place to pin that branch: the assertion used to live in the end-to-end smoke test against
    /// run-profile, and when run-profile was implemented the branch was left with no coverage at all.</summary>
    [Fact]
    public async Task An_unregistered_request_type_answers_not_implemented()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            // No handler for list-profiles is registered on this server.
            var answered = await client!.RequestAsync<ProfileListResponse>(new ListProfilesRequest());
            Assert.True(answered.TryGetError(out IpcError? error));
            Assert.Equal("NOT_IMPLEMENTED", error!.Code);

            // The connection stays usable — an unknown type is a per-request answer, not a fault.
            var ok = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(ok.TryGetValue(out _));
        }
    }

    [Fact]
    public async Task Disposing_the_client_under_an_in_flight_request_yields_a_failure_value_not_a_throw()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));

        Task<FileManager.Contracts.Primitives.Result<StatusResponse, IpcError>> inFlight =
            client!.RequestAsync<StatusResponse>(new GetStatusRequest());
        await Task.Delay(50);                        // let the request get onto the pipe
        await client.DisposeAsync();                 // the gateway's drop-on-transport-death race

        var result = await inFlight;                 // must complete as a value, never throw
        Assert.True(result.IsCanceled || result.TryGetError(out _));
    }

    [Fact]
    public async Task A_malformed_length_prefix_drops_that_connection_and_the_server_keeps_serving()
    {
        // A hostile/buggy peer writes a negative frame length; the server must reject the frame,
        // close only that connection, and keep accepting.
        using (var raw = new NamedPipeClientStream(
            ".", IpcEndpoint.Resolve(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await raw.ConnectAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            await raw.WriteAsync(new byte[] { 0xFB, 0xFF, 0xFF, 0xFF });   // length = -5
            await raw.FlushAsync();
            // The server ends the connection; the read observing that is enough — no assertion on
            // exactly how the close manifests client-side.
            try
            {
                int read = await raw.ReadAsync(new byte[1]).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, read);
            }
            catch (IOException)
            {
                // Broken pipe is an equally valid manifestation of the server-side close.
            }
        }

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            var ok = await client!.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(ok.TryGetValue(out _));
        }
    }
}

/// <summary>The squatting-DoS shape of the accept path, isolated from a running server: when the
/// predictable pipe name is already claimed with exclusive instancing, AcceptAsync must return a
/// retryable failure value — a thrown constructor exception would kill the server's accept loop
/// permanently (it only retries failure values).</summary>
[SupportedOSPlatform("windows")]
[Collection(PipeCollection.Name)]
public sealed class IpcEndpointSquatterTests
{
    [Fact]
    public async Task A_squatted_pipe_name_is_a_retryable_accept_failure_not_a_thrown_exception()
    {
        string name = "fm-squat-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", name);
        try
        {
            // Claim the name with maxNumberOfServerInstances: 1 — the provider's own constructor
            // (MaxAllowedServerInstances) then fails to create a second instance.
            using var squatter = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var provider = new WindowsIpcEndpointProvider(NullLogger<WindowsIpcEndpointProvider>.Instance);
            var accepted = await provider.AcceptAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            Assert.True(accepted.TryGetError(out string? error));   // a value, not a throw
            Assert.Contains("could not listen", error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", null);
        }
    }
}
