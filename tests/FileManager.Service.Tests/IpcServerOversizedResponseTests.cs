using FileManager.Contracts.IPC;
using FileManager.Core.IPC;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.Versioning;

namespace FileManager.Service.Tests;

/// <summary>Pipe tests share the process-wide FILEMANAGER_PIPE_NAME environment variable, so
/// they must not run in parallel with each other.</summary>
[CollectionDefinition(PipeCollection.Name)]
public sealed class PipeCollection
{
    public const string Name = "ipc-pipe";
}

/// <summary>A handler response bigger than the 16 MiB frame cap must come back as a readable
/// IPC_RESPONSE_TOO_LARGE error — not kill the connection so the client sees only
/// "connection closed" (the original stress-test dry-run failure mode).</summary>
[SupportedOSPlatform("windows")]
[Collection(PipeCollection.Name)]
public sealed class IpcServerOversizedResponseTests : IAsyncLifetime
{
    private IpcServer? _server;

    /// <summary>Answers get-job-log with a payload guaranteed to serialize over the frame cap
    /// and get-status with a normal small response.</summary>
    private sealed class HugeJobLogHandler : IIpcRequestHandler
    {
        public string RequestType => IpcRequestTypes.GetJobLog;

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
            Task.FromResult<IpcResponse>(new JobLogResponse
            {
                Lines = [new string('x', IpcFrameCodec.MaxPayloadBytes + 1024)],
            });
    }

    private sealed class OkStatusHandler : IIpcRequestHandler
    {
        public string RequestType => IpcRequestTypes.GetStatus;

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
            Task.FromResult<IpcResponse>(new StatusResponse
            {
                Status = new EngineStatusSnapshot(false, 0, 0, 0, null),
            });
    }

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", "fm-oversize-" + Guid.NewGuid().ToString("N"));
        IIpcRequestHandler[] handlers = [new HugeJobLogHandler(), new OkStatusHandler()];
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
    public async Task Oversized_response_is_a_readable_error_and_the_connection_survives()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            var oversized = await client!.RequestAsync<JobLogResponse>(new GetJobLogRequest { JobId = Guid.NewGuid() });
            Assert.True(oversized.TryGetError(out IpcError? error));
            Assert.Equal("IPC_RESPONSE_TOO_LARGE", error!.Code);
            Assert.Contains("get-job-log", error.Message);

            // The same connection still serves the next request — it was not dropped.
            var followUp = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(followUp.TryGetValue(out StatusResponse? status));
            Assert.Equal(0, status!.Status.ActiveProfiles);
        }
    }
}
