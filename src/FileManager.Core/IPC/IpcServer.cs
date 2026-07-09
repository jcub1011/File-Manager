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
        try
        {
            await AcceptLoopCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            // Last resort: this task is otherwise unobserved until StopAsync — without this
            // the service silently stops accepting connections.
            logger.LogCritical(ex, "IPC accept loop failed unexpectedly; the service can no longer accept connections");
        }
    }

    private async Task AcceptLoopCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Result<Stream, string> accepted = await endpoint.AcceptAsync(ct).ConfigureAwait(false);
            if (accepted.IsCanceled)
                return;

            if (accepted.TryGetError(out string? acceptError))
            {
                if (ct.IsCancellationRequested)
                    return;
                logger.LogWarning("IPC accept failed: {Error}; retrying", acceptError);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;         // shutdown during the retry backoff
                }
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
                    if (frame.IsCanceled)
                        return;                     // shutdown
                    if (frame.TryGetError(out string? readError))
                    {
                        logger.LogDebug("IPC connection ended: {Reason}", readError);
                        return;
                    }
                    frame.TryGetValue(out byte[]? payload);

                    Resolution resolution = Resolve(payload!);
                    if (resolution.Error is not null)
                    {
                        // Parse / version / not-implemented failure: one frame, then keep serving.
                        if (!await TryWriteResponseFrameAsync(stream, resolution.RequestType, resolution.Error, ct).ConfigureAwait(false))
                            return;
                        continue;
                    }

                    if (resolution.Handler is IIpcStreamingRequestHandler streaming)
                    {
                        // One request, many response frames (§ streamed dry-run). The connection
                        // survives a normal or errored stream; only a transport fault ends it.
                        if (!await ServeStreamAsync(stream, resolution.RequestType, streaming, resolution.Request!, ct).ConfigureAwait(false))
                            return;
                        continue;
                    }

                    IpcResponse response = await InvokeAsync(resolution.RequestType, resolution.Handler!, resolution.Request!, ct).ConfigureAwait(false);
                    if (!await TryWriteResponseFrameAsync(stream, resolution.RequestType, response, ct).ConfigureAwait(false))
                        return;
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
            catch (Exception ex)
            {
                // Last resort: this task is otherwise unobserved — without this the connection
                // dies with no trace and the client sees only "connection closed".
                logger.LogError(ex, "IPC connection handler failed unexpectedly; closing this connection");
            }
        }
    }

    /// <summary>The outcome of parsing + routing one request frame: either an <see cref="Error"/>
    /// response to send once, or a resolved <see cref="Handler"/> and <see cref="Request"/> to serve.</summary>
    private readonly record struct Resolution(string RequestType, IpcRequest? Request, IIpcRequestHandler? Handler, IpcResponse? Error);

    /// <summary>Parses the frame, checks the protocol version, and looks up the handler — the shared
    /// front half of both the single-response and streaming paths.</summary>
    private Resolution Resolve(byte[] payload)
    {
        Result<IpcRequest, string> parsed = IpcSerializer.DeserializeRequest(payload);
        if (parsed.TryGetError(out string? parseError))
        {
            logger.LogWarning("IPC request frame rejected: {Reason}", parseError);
            return new Resolution("<malformed>", null, null,
                new ErrorResponse { Code = "IPC_MALFORMED", Message = "the request frame could not be parsed" });
        }
        parsed.TryGetValue(out IpcRequest? request);

        string discriminator = IpcRequestTypes.DiscriminatorOf(request!);
        if (request!.ProtocolVersion != 1)
            return new Resolution(discriminator, null, null, new ErrorResponse
            {
                Code = "IPC_VERSION_MISMATCH",
                Message = $"this service speaks protocol version 1, the client sent {request.ProtocolVersion}",
            });

        if (!handlers.TryGetValue(discriminator, out IIpcRequestHandler? handler))
            return new Resolution(discriminator, null, null, new ErrorResponse
            {
                Code = "NOT_IMPLEMENTED",
                Message = $"\"{discriminator}\" is not available in this build",
            });

        return new Resolution(discriminator, request, handler, null);
    }

    /// <summary>Invokes a single-response handler, turning a handler fault into an INTERNAL_ERROR
    /// response (cancellation still propagates as shutdown).</summary>
    private async Task<IpcResponse> InvokeAsync(string requestType, IIpcRequestHandler handler, IpcRequest request, CancellationToken ct)
    {
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
            logger.LogError(ex, "Handler for {RequestType} threw", requestType);
            return new ErrorResponse { Code = "INTERNAL_ERROR", Message = ex.Message };
        }
    }

    /// <summary>Serves a streaming handler: one response frame per yielded item, then the connection
    /// survives to serve the next request. A handler fault becomes a trailing INTERNAL_ERROR frame;
    /// cancellation (shutdown) propagates. Returns false only when the connection must close (a
    /// transport write failed or was cancelled).</summary>
    private async Task<bool> ServeStreamAsync(
        Stream stream, string requestType, IIpcStreamingRequestHandler handler, IpcRequest request, CancellationToken ct)
    {
        IAsyncEnumerator<IpcResponse> enumerator = handler.HandleStreamAsync(request, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                IpcResponse response;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        return true;                // stream complete — keep the connection
                    response = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;                          // shutdown — let ServeConnectionAsync end the connection
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Streaming handler for {RequestType} threw", requestType);
                    // Best-effort terminal error frame; the result is moot (we stop either way).
                    await TryWriteResponseFrameAsync(stream, requestType,
                        new ErrorResponse { Code = "INTERNAL_ERROR", Message = ex.Message }, ct).ConfigureAwait(false);
                    return true;
                }

                if (!await TryWriteResponseFrameAsync(stream, requestType, response, ct).ConfigureAwait(false))
                    return false;                   // transport write failed/cancelled — close
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Serializes and writes one response frame, swapping an oversized payload for a
    /// readable IPC_RESPONSE_TOO_LARGE error (WriteFrameAsync would otherwise throw). Returns false
    /// when the connection should close (write failed or was cancelled).</summary>
    private async Task<bool> TryWriteResponseFrameAsync(Stream stream, string requestType, IpcResponse response, CancellationToken ct)
    {
        byte[] responseBytes = IpcSerializer.SerializeResponse(response);
        if (responseBytes.Length > IpcFrameCodec.MaxPayloadBytes)
        {
            logger.LogWarning(
                "IPC response to \"{RequestType}\" is {Size:N0} bytes, over the {Cap:N0}-byte frame cap; replying IPC_RESPONSE_TOO_LARGE",
                requestType, responseBytes.Length, IpcFrameCodec.MaxPayloadBytes);
            responseBytes = IpcSerializer.SerializeResponse(new ErrorResponse
            {
                Code = "IPC_RESPONSE_TOO_LARGE",
                Message = $"the response to \"{requestType}\" was {responseBytes.Length:N0} bytes, over the " +
                    $"{IpcFrameCodec.MaxPayloadBytes:N0}-byte IPC frame limit — narrow the request " +
                    "(for a dry run, set a scope folder) and retry",
            });
        }

        Result writeResult = await IpcFrameCodec.WriteFrameAsync(stream, responseBytes, ct).ConfigureAwait(false);
        if (writeResult.IsCanceled)
            return false;                           // shutdown
        if (writeResult.TryGetError(out string? writeError))
        {
            logger.LogDebug("IPC connection ended: {Reason}", writeError);
            return false;
        }
        return true;
    }
}
