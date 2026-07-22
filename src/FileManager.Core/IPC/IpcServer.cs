using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
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
    // A slow/dead event subscriber must never block the publisher (§8): each holds a bounded channel
    // of pre-serialized frames, dropping the oldest under back-pressure (event delivery is
    // best-effort — the activity view/tray tolerate a gap).
    private const int SubscriberQueueCapacity = 1024;

    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly ConcurrentDictionary<Subscriber, byte> _subscribers = new();
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

    /// <summary>Fans an engine event out to every subscribed connection (§4.9, §4.10). Serializes
    /// once, then non-blocking-writes the frame to each subscriber's bounded channel — a per-connection
    /// writer task drains it. Never blocks on a slow/dead subscriber (§8).</summary>
    public void Broadcast(EngineEvent evt)
    {
        if (_subscribers.IsEmpty)
            return;

        byte[] frame;
        try
        {
            frame = IpcSerializer.SerializeEvent(evt);
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: a serialization fault must not take down the publisher.
            logger.LogError(ex, "Failed to serialize engine event {Type}; dropping it", evt.GetType().Name);
            return;
        }

        foreach (Subscriber subscriber in _subscribers.Keys)
            subscriber.Channel.Writer.TryWrite(frame);   // DropOldest — always accepts, never blocks
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
            // No scheduling token: with one, a cancellation racing the accept could skip
            // ServeConnectionAsync entirely and leak the accepted pipe handle (its `await using`
            // is what disposes the stream). The connection observes ct itself and ends promptly.
            Task connection = Task.Run(() => ServeConnectionAsync(stream!, ct));
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
                        // A clean close at a frame boundary is routine (client disconnected);
                        // anything else — torn frame, bad length, I/O error — is the abnormal
                        // connection death that must be visible at production log levels.
                        if (readError == IpcFrameCodec.ConnectionClosedMessage)
                            logger.LogDebug("IPC connection closed");
                        else
                            logger.LogWarning("IPC connection ended abnormally: {Reason}", readError);
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

                    if (resolution.IsSubscribe)
                    {
                        // Event subscription (§3.2): ack once, then this connection becomes a one-way
                        // event stream until the client disconnects or the server stops.
                        await ServeEventSubscriptionAsync(stream, ct).ConfigureAwait(false);
                        return;
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
                // Warning, not Debug: the service logs at Information in production, and a dropped
                // connection that only shows at Debug is exactly the silent-death failure mode the
                // catch-and-log directive exists to prevent.
                logger.LogWarning(ex, "IPC connection dropped");
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
    private readonly record struct Resolution(
        string RequestType, IpcRequest? Request, IIpcRequestHandler? Handler, IpcResponse? Error, bool IsSubscribe = false);

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
        if (request!.ProtocolVersion != IpcRequest.CurrentProtocolVersion)
            return new Resolution(discriminator, null, null, new ErrorResponse
            {
                Code = "IPC_VERSION_MISMATCH",
                Message = $"this service speaks protocol version {IpcRequest.CurrentProtocolVersion}, the client sent {request.ProtocolVersion}",
            });

        // Event subscription is served by the connection loop directly (open-ended, Broadcast-driven),
        // not through the request/response dispatch table.
        if (discriminator == IpcRequestTypes.Subscribe)
            return new Resolution(discriminator, request, null, null, IsSubscribe: true);

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

    /// <summary>Serves an event subscription: acknowledges with an <see cref="OkResponse"/> (the
    /// client contract), then drains this subscriber's bounded channel to the wire as one event frame
    /// each until the client disconnects (a write fails) or the server stops. A subscriber left idle
    /// after a client vanishes is reaped on the next event's failed write, or at shutdown.</summary>
    private async Task ServeEventSubscriptionAsync(Stream stream, CancellationToken ct)
    {
        if (!await TryWriteResponseFrameAsync(stream, IpcRequestTypes.Subscribe, new OkResponse(), ct).ConfigureAwait(false))
            return;

        Subscriber subscriber = new();
        _subscribers.TryAdd(subscriber, 0);
        try
        {
            await foreach (byte[] frame in subscriber.Channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Result write = await IpcFrameCodec.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
                if (write.IsCanceled)
                    return;
                if (write.TryGetError(out string? writeError))
                {
                    logger.LogDebug("Event subscription ended: {Reason}", writeError);
                    return;
                }
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriber, out _);
        }
    }

    /// <summary>One event-subscription connection: a bounded, drop-oldest channel of pre-serialized
    /// event frames the connection's writer task drains.</summary>
    private sealed class Subscriber
    {
        public Channel<byte[]> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(SubscriberQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
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
            // A failed mid-response write means the client sees a torn reply — abnormal, so it
            // must be visible at production log levels (Warning, not Debug).
            logger.LogWarning("IPC connection ended writing a {RequestType} response: {Reason}", requestType, writeError);
            return false;
        }
        return true;
    }
}
