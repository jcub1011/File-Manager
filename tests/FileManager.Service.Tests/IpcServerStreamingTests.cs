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
    /// "error-midway" yields an ErrorResponse after the second chunk; "throw" faults immediately;
    /// "throw-midway" faults after the second chunk has already been sent.</summary>
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

            // One builder across all chunks — each chunk's Directories slice carries only its
            // first-referenced entries, exercising the client's cross-chunk table assembly.
            DryRunDirectoryTableBuilder dirs = new();
            for (int b = 0; b < 3; b++)
            {
                List<DryRunFile> files = Enumerable.Range(0, 4)
                    .Select(i => dirs.Convert(new PhysicalFile
                    {
                        Path = $@"C:\src\b{b}\f{i}.dat",
                        Root = @"C:\src",
                        Length = 0,
                        LastWritten = DateTimeOffset.UnixEpoch,
                    }))
                    .ToList();
                List<DryRunOperation> ops = Enumerable.Range(0, 4)
                    .Select(i => dirs.Convert(new VirtualFileOperation
                    {
                        Path = $@"C:\src\b{b}\f{i}.dat",
                        Root = @"C:\src",
                        Kind = OperationKind.Processed,
                        SourceIndex = b * 4 + i,
                    }))
                    .ToList();
                yield return DryRunColumns.ToChunk(dirs.FlushNew(), sourceFiles: files, sourceOperations: ops);
                // An informational progress frame after every chunk: the client must relay it to the
                // caller's IProgress and reassemble the report exactly as if it were not there.
                yield return new DryRunProgressResponse
                {
                    Phase = DryRunProgressPhase.ScanningSources,
                    SourceFiles = (b + 1) * 4,
                    DestinationFiles = 0,
                };

                if (typed.ScopePath == "error-midway" && b == 1)
                {
                    yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = "midway failure" };
                    yield break;
                }
                if (typed.ScopePath == "throw-midway" && b == 1)
                    throw new InvalidOperationException("boom-midway");
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
            Assert.Equal(12, report!.SourceFiles.Count);
            Assert.False(report.Truncated);
            string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
            string PathOf(DryRunFile f) => Path.Join(dirPaths[f.DirIndex], f.FileName);
            Assert.Equal(@"C:\src\b0\f0.dat", PathOf(report.SourceFiles[0]));   // order preserved across chunks
            Assert.Equal(@"C:\src\b2\f3.dat", PathOf(report.SourceFiles[^1]));

            // The same connection still serves the next request — the stream did not desync it.
            var followUp = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(followUp.TryGetValue(out _));
        }
    }

    /// <summary>Synchronous relay — unlike Progress&lt;T&gt; (which posts to the pool when there is no
    /// UI context), reports land in the sink before DryRunStreamAsync returns, so asserts are
    /// race-free.</summary>
    private sealed class CollectingProgress(List<DryRunProgress> sink) : IProgress<DryRunProgress>
    {
        public void Report(DryRunProgress value) => sink.Add(value);
    }

    [Fact]
    public async Task Interleaved_progress_frames_reach_the_callback_and_leave_the_report_intact()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            List<DryRunProgress> updates = [];
            var result = await client!.DryRunStreamAsync(
                new DryRunStreamRequest { ProfileId = Guid.NewGuid() }, new CollectingProgress(updates));

            Assert.True(result.TryGetValue(out DryRunReport? report));
            Assert.Equal(12, report!.SourceFiles.Count);   // reassembly untouched by the extra frames
            Assert.Equal([4, 8, 12], updates.Select(u => u.SourceFiles).ToArray());
            Assert.All(updates, u => Assert.Equal(DryRunProgressPhase.ScanningSources, u.Phase));
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

    [Fact]
    public async Task Handler_fault_after_a_chunk_discards_partial_data_as_an_error_and_the_connection_survives()
    {
        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            // Chunks were already sent before the fault; the client must surface the terminal error
            // rather than a partial "success", and the connection must still serve the next request.
            var result = await client!.DryRunStreamAsync(new DryRunStreamRequest { ProfileId = Guid.NewGuid(), ScopePath = "throw-midway" });
            Assert.True(result.TryGetError(out IpcError? error));
            Assert.Equal("INTERNAL_ERROR", error!.Code);

            var followUp = await client.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(followUp.TryGetValue(out _));
        }
    }
}
