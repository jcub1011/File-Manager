using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC;

/// <summary>The IPC server (§4.9): one accept loop, one task per connection, requests on a
/// connection handled strictly sequentially (§3.2). Dispatch is a table keyed by the wire
/// discriminator — an unregistered request type answers NOT_IMPLEMENTED, which is how every
/// out-of-scope request (run-profile, subscribe, …) responds in this slice.</summary>
public sealed class IpcServer(
    ILogger<IpcServer> logger,
    IIpcEndpointProvider endpoint,
    IReadOnlyDictionary<string, IIpcRequestHandler> handlers) : IIpcServer
{
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public Result Start()
    {
        if (_acceptLoop is not null)
            return "the IPC server is already running";
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        logger.LogInformation("IPC server started");
        return Result.Success();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is null || _acceptLoop is null)
            return;
        _cts.Cancel();
        try
        {
            await _acceptLoop.WaitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(_connections.Keys).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stop was itself cancelled — connections tear down with the CTS regardless
        }
        _cts.Dispose();
        _cts = null;
        _acceptLoop = null;
        logger.LogInformation("IPC server stopped");
    }

    /// <summary>Structural no-op in this slice: the subscriber registry exists so event
    /// subscription (SubscribeEventsRequest) slots in without reshaping the class, but nothing
    /// registers yet — the dispatch table answers "subscribe" with NOT_IMPLEMENTED.</summary>
    public void Broadcast(EngineEvent evt)
    {
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Result<Stream, string> accepted;
            try
            {
                accepted = await endpoint.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (accepted.TryGetError(out string? acceptError))
            {
                if (ct.IsCancellationRequested)
                    return;
                logger.LogWarning("IPC accept failed: {Error}; retrying", acceptError);
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                continue;
            }

            accepted.TryGetValue(out Stream? stream);
            Task connection = Task.Run(() => ServeConnectionAsync(stream!, ct), ct);
            _connections.TryAdd(connection, 0);
            _ = connection.ContinueWith(
                t => _connections.TryRemove(t, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Result<byte[], string> frame = await IpcFrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    if (frame.TryGetError(out string? readError))
                    {
                        logger.LogDebug("IPC connection ended: {Reason}", readError);
                        return;
                    }
                    frame.TryGetValue(out byte[]? payload);

                    IpcResponse response = await DispatchAsync(payload!, ct).ConfigureAwait(false);
                    await IpcFrameCodec.WriteFrameAsync(stream, IpcSerializer.SerializeResponse(response), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "IPC connection dropped");
            }
        }
    }

    private async Task<IpcResponse> DispatchAsync(byte[] payload, CancellationToken ct)
    {
        IpcRequest? request = IpcSerializer.DeserializeRequest(payload);
        if (request is null)
            return new ErrorResponse { Code = "IPC_MALFORMED", Message = "the request frame could not be parsed" };

        if (request.ProtocolVersion != 1)
            return new ErrorResponse
            {
                Code = "IPC_VERSION_MISMATCH",
                Message = $"this service speaks protocol version 1, the client sent {request.ProtocolVersion}",
            };

        string discriminator = IpcRequestTypes.DiscriminatorOf(request);
        if (!handlers.TryGetValue(discriminator, out IIpcRequestHandler? handler))
            return new ErrorResponse
            {
                Code = "NOT_IMPLEMENTED",
                Message = $"\"{discriminator}\" is not available in this build",
            };

        try
        {
            return await handler.HandleAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handler for {RequestType} threw", discriminator);
            return new ErrorResponse { Code = "INTERNAL_ERROR", Message = ex.Message };
        }
    }
}
