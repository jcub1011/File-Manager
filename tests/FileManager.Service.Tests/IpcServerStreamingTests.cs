using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.IPC;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace FileManager.Service.Tests;

/// <summary>A streaming handler's one request must come back as many response frames reassembled
/// into a full report over a real named pipe — and, exactly like a single response, a mid-stream
/// error or a handler fault must be a readable error the connection survives (not a silent drop).</summary>
[SupportedOSPlatform("windows")]
[Collection(PipeCollection.Name)]
public sealed class IpcServerStreamingTests : IAsyncLifetime
{
    private IpcServer? _server;

    /// <summary>Streams three 4-file chunks then a completion. ScopePath is a test control channel:
    /// "error-midway" yields an ErrorResponse after the second chunk; "throw" faults immediately.</summary>
    private sealed class ChunkedDryRunHandler : IIpcStreamingRequestHandler
    {
        public string RequestType => IpcRequestTypes.DryRunStream;

        public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("streaming handler");

        public async IAsyncEnumerable<IpcResponse> HandleStreamAsync(
            IpcRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var typed = (DryRunStreamRequest)request;
            if (typed.ScopePath == "throw")
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }

            for (int b = 0; b < 3; b++)
            {
                List<DryRunFileResult> files = Enumerable.Range(0, 4)
                    .Select(i => new DryRunFileResult
                    {
                        SourcePath = $@"C:\src\b{b}\f{i}.dat",
                        Disposition = DryRunFileDisposition.WouldProcess,
                    })
                    .ToList();
                yield return new DryRunChunkResponse { Files = files };

                if (typed.ScopePath == "error-midway" && b == 1)
                {
                    yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = "midway failure" };
                    yield break;
                }
                await Task.Yield();
            }

            yield return new DryRunCompleteResponse { GeneratedAt = DateTimeOffset.UnixEpoch, Truncated = false };
        }
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
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", "fm-stream-" + Guid.NewGuid().ToString("N"));
        IIpcRequestHandler[] handlers = [new ChunkedDryRunHandler(), new OkStatusHandler()];
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
    public async Task Streamed_report_reassembles_in_order_and_the_connection_survives()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            var result = await client!.DryRunStreamAsync(new DryRunStreamRequest { ProfileId = Guid.NewGuid() });
            Assert.True(result.TryGetValue(out DryRunReport? report));
            Assert.Equal(12, report!.Files.Count);
            Assert.False(report.Truncated);
            Assert.Equal(@"C:\src\b0\f0.dat", report.Files[0].SourcePath);   // order preserved across chunks
            Assert.Equal(@"C:\src\b2\f3.dat", report.Files[^1].SourcePath);

            // The same connection still serves the next request — the stream did not desync it.
            var followUp = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(followUp.TryGetValue(out _));
        }
    }

    [Fact]
    public async Task Mid_stream_error_frame_surfaces_as_an_IpcError_and_the_connection_survives()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            var result = await client!.DryRunStreamAsync(new DryRunStreamRequest { ProfileId = Guid.NewGuid(), ScopePath = "error-midway" });
            Assert.True(result.TryGetError(out IpcError? error));
            Assert.Equal("DRY_RUN_FAILED", error!.Code);

            var followUp = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(followUp.TryGetValue(out _));
        }
    }

    [Fact]
    public async Task Handler_fault_mid_stream_is_a_readable_error_and_the_connection_survives()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            var result = await client!.DryRunStreamAsync(new DryRunStreamRequest { ProfileId = Guid.NewGuid(), ScopePath = "throw" });
            Assert.True(result.TryGetError(out IpcError? error));
            Assert.Equal("INTERNAL_ERROR", error!.Code);

            var followUp = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(followUp.TryGetValue(out _));
        }
    }
}
