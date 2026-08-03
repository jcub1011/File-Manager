using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

/// <summary>One IPC connection. Requests on a connection are strictly sequential (spec §2.1 /
/// architecture §3.2); open parallel clients for concurrency. After SubscribeAsync the
/// connection becomes a one-way event stream and must not be used for requests.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private volatile bool _poisoned;

    private IpcClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>True once request/response framing can no longer be trusted: a request was
    /// cancelled after its write began (the response is still queued on the pipe — or the frame
    /// itself is torn), or a response arrived malformed / of an unexpected type. Every further
    /// request would read the previous request's leftovers, so callers must discard this client
    /// and connect a fresh one. Checked at the top of <see cref="RequestAsync"/>.</summary>
    public bool IsPoisoned => _poisoned;

    /// <summary>Connects with a short timeout so the §3.3 probe-then-start handshake stays fast.
    /// Failure to connect is an expected state (service not running), not an exception.</summary>
    public static async Task<Result<IpcClient, string>> ConnectAsync(CancellationToken ct = default)
    {
        NamedPipeClientStream pipe = new(
            ".", IpcEndpoint.Resolve(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return new IpcClient(pipe);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return "timed out connecting to the service pipe";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return $"could not connect to the service pipe: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return Result<IpcClient, string>.Canceled();
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            await pipe.DisposeAsync().ConfigureAwait(false);
            return $"could not connect to the service pipe: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Sends one request and awaits its single response. An ErrorResponse — and any
    /// transport fault — surfaces as an IpcError; a response of an unexpected type is
    /// IPC_UNEXPECTED_RESPONSE. Pass TResponse = IpcResponse to receive any non-error response
    /// (used where one request has several success shapes, e.g. SaveProfile).</summary>
    public async Task<Result<TResponse, IpcError>> RequestAsync<TResponse>(
        IpcRequest request, CancellationToken ct = default) where TResponse : IpcResponse
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the gate was acquired — nothing to release.
            return Result<TResponse, IpcError>.Canceled();
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a concurrent DisposeAsync can dispose the pipe
            // under a parked waiter; that must surface as a failure value, never an unhandled throw.
            return new IpcError("IPC_TRANSPORT", $"connection is shutting down: {ex.GetType().Name}: {ex.Message}");
        }

        // Everything from the first write byte onward runs poisoned-by-default: only an outcome
        // that provably consumed exactly one whole response frame (a typed response or an
        // ErrorResponse) re-arms the connection for the next request. A cancellation mid-write
        // tears the frame; one mid-read leaves the response queued — either way the NEXT request
        // would read this request's leftovers, so the client refuses further use instead.
        if (_poisoned)
        {
            ReleaseGate();
            return new IpcError("IPC_TRANSPORT", "the connection was desynchronized by an earlier cancellation and must be reopened");
        }

        try
        {
            byte[] payload = IpcSerializer.SerializeRequest(request);
            _poisoned = true;
            Result writeResult = await IpcFrameCodec.WriteFrameAsync(_pipe, payload, ct).ConfigureAwait(false);
            if (writeResult.IsCanceled)
                return Result<TResponse, IpcError>.Canceled();
            if (writeResult.TryGetError(out string? writeError))
                return new IpcError("IPC_TRANSPORT", writeError);

            Result<byte[], string> frame = await IpcFrameCodec.ReadFrameAsync(_pipe, ct).ConfigureAwait(false);
            if (frame.IsCanceled)
                return Result<TResponse, IpcError>.Canceled();
            if (frame.TryGetError(out string? transportError))
                return new IpcError("IPC_TRANSPORT", transportError);
            frame.TryGetValue(out byte[]? bytes);

            Result<IpcResponse, string> parsed = IpcSerializer.DeserializeResponse(bytes!);
            if (parsed.TryGetError(out string? parseError))
                return new IpcError("IPC_MALFORMED", $"the service sent a response that could not be parsed: {parseError}");
            parsed.TryGetValue(out IpcResponse? response);
            switch (response!)
            {
                case ErrorResponse error:
                    _poisoned = false;               // a whole frame was consumed — still in sync
                    return new IpcError(error.Code, error.Message);
                case TResponse typed:
                    _poisoned = false;
                    return typed;
                default:
                    // A whole frame was consumed but of the wrong type: request/response
                    // correlation can no longer be trusted — stay poisoned.
                    return new IpcError("IPC_UNEXPECTED_RESPONSE",
                        $"expected {typeof(TResponse).Name}, got {response!.GetType().Name}");
            }
        }
        catch (OperationCanceledException)
        {
            return Result<TResponse, IpcError>.Canceled();
        }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException)
        {
            return new IpcError("IPC_TRANSPORT", ex.Message);
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return new IpcError("IPC_INTERNAL", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>Releases the request gate, tolerating a concurrent DisposeAsync having disposed
    /// the semaphore — the request's outcome (already computed) must win over a teardown race.</summary>
    private void ReleaseGate()
    {
        try
        {
            _requestGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent DisposeAsync tore the client down while this request was in flight.
        }
    }

    /// <summary>Sends a streaming dry-run request and reassembles the chunk frames
    /// (<see cref="DryRunChunkResponse"/>) into one <see cref="DryRunReport"/>, stopping at the
    /// <see cref="DryRunCompleteResponse"/> terminator. Interleaved <see cref="DryRunProgressResponse"/>
    /// frames are relayed to <paramref name="progress"/> (when supplied) without touching reassembly.
    /// Because the report arrives as many small
    /// frames it is not bounded by the single-frame size cap. An ErrorResponse (e.g. PROFILE_NOT_FOUND,
    /// DRY_RUN_FAILED) and every transport fault surface as an <see cref="IpcError"/>; cancellation is
    /// a Canceled result, never a throw. Holds the request gate for the whole stream (§3.2).
    /// A Canceled result abandons the stream mid-flight: the service keeps writing the remaining
    /// chunk and completion frames, so those bytes are still queued on the pipe. This connection must
    /// therefore be discarded after a cancellation (as callers do — one connection per dry run);
    /// reusing it for another request would read the stale frames and desync the protocol.</summary>
    public async Task<Result<DryRunReport, IpcError>> DryRunStreamAsync(
        DryRunStreamRequest request, IProgress<DryRunProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReportAssemblySink sink = new();
        Result<DryRunCompletion, IpcError> outcome =
            await DryRunStreamAsync(request, sink, progress, ct).ConfigureAwait(false);

        if (outcome.IsCanceled)
            return Result<DryRunReport, IpcError>.Canceled();
        if (outcome.TryGetError(out IpcError? error))
            return error;
        outcome.TryGetValue(out DryRunCompletion? completion);
        return sink.ToReport(request.ProfileId, completion!);
    }

    /// <summary>The streaming core of <see cref="DryRunStreamAsync(DryRunStreamRequest, IProgress{DryRunProgress}?, CancellationToken)"/>:
    /// pushes each validated chunk frame to <paramref name="sink"/> and returns only the run-level
    /// facts from the terminator, so a consumer that folds chunks as they arrive never holds the
    /// assembled report. See <see cref="IDryRunChunkSink"/> for the sink's contract; the framing,
    /// gating, poisoning and error semantics are identical to the assembling overload — which is
    /// implemented on top of this method, so there is one reassembly loop, not two.</summary>
    public async Task<Result<DryRunCompletion, IpcError>> DryRunStreamAsync(
        DryRunStreamRequest request, IDryRunChunkSink sink,
        IProgress<DryRunProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        try
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<DryRunCompletion, IpcError>.Canceled();
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a concurrent DisposeAsync can dispose the pipe
            // under a parked waiter; that must surface as a failure value, never an unhandled throw.
            return new IpcError("IPC_TRANSPORT", $"connection is shutting down: {ex.GetType().Name}: {ex.Message}");
        }

        if (_poisoned)
        {
            ReleaseGate();
            return new IpcError("IPC_TRANSPORT", "the connection was desynchronized by an earlier cancellation and must be reopened");
        }

        try
        {
            byte[] payload = IpcSerializer.SerializeRequest(request);
            _poisoned = true;   // re-armed only by a complete, in-sync stream outcome (see RequestAsync)
            Result writeResult = await IpcFrameCodec.WriteFrameAsync(_pipe, payload, ct).ConfigureAwait(false);
            if (writeResult.IsCanceled)
                return Result<DryRunCompletion, IpcError>.Canceled();
            if (writeResult.TryGetError(out string? writeError))
                return new IpcError("IPC_TRANSPORT", writeError);

            // The running directory count is all the trust-boundary check needs — the entries
            // themselves go to the sink, which is free to resolve them and drop them. Indices stay
            // global because chunks are handed over in receive order.
            int directoryCount = 0;
            while (true)
            {
                Result<byte[], string> frame = await IpcFrameCodec.ReadFrameAsync(_pipe, ct).ConfigureAwait(false);
                if (frame.IsCanceled)
                    return Result<DryRunCompletion, IpcError>.Canceled();
                if (frame.TryGetError(out string? transportError))
                    return new IpcError("IPC_TRANSPORT", transportError);
                frame.TryGetValue(out byte[]? bytes);

                Result<IpcResponse, string> parsed = IpcSerializer.DeserializeResponse(bytes!);
                if (parsed.TryGetError(out string? parseError))
                    return new IpcError("IPC_MALFORMED", $"the service sent a response that could not be parsed: {parseError}");
                parsed.TryGetValue(out IpcResponse? response);

                switch (response!)
                {
                    case DryRunChunkResponse chunk:
                        // Fail loud on a malformed table rather than mis-rooting paths: every parent
                        // must already be in the assembled table (parents precede children globally).
                        foreach (DryRunDirectory dir in chunk.Directories)
                        {
                            if (dir.ParentIndex < -1 || dir.ParentIndex >= directoryCount)
                                return new IpcError("IPC_MALFORMED",
                                    $"the service sent a directory entry ('{dir.Name}') whose ParentIndex {dir.ParentIndex} does not precede it in the table");
                            directoryCount++;
                        }
                        // Every file/op index must resolve against the table assembled so far
                        // (directories precede the records that reference them). Reject an
                        // out-of-range index here rather than letting a consumer crash on it.
                        if (DryRunDirectoryTable.FindInvalidReference(
                                directoryCount, chunk.SourceFiles, chunk.SourceOperations) is { } badSource)
                            return new IpcError("IPC_MALFORMED",
                                $"the service sent a record with an out-of-range directory index: {badSource}");
                        if (DryRunDirectoryTable.FindInvalidReference(
                                directoryCount, chunk.DestinationFiles, chunk.DestinationOperations) is { } badDest)
                            return new IpcError("IPC_MALFORMED",
                                $"the service sent a record with an out-of-range directory index: {badDest}");
                        // Only after the chunk has cleared the trust boundary. A throwing sink is a
                        // consumer bug, not a protocol fault, so it does not un-poison the connection
                        // (the remaining frames are still queued on the pipe either way).
                        try
                        {
                            sink.OnChunk(chunk);
                        }
                        catch (Exception ex)
                        {
                            return new IpcError("IPC_SINK_FAILED",
                                $"the dry-run chunk sink threw: {ex.GetType().Name}: {ex.Message}");
                        }
                        break;
                    case DryRunCompleteResponse complete:
                        _poisoned = false;           // terminator consumed — connection in sync
                        return new DryRunCompletion(complete.GeneratedAt, complete.Truncated, complete.Space);
                    case DryRunProgressResponse progressFrame:
                        // Informational only — reassembly state is untouched, the loop just continues.
                        progress?.Report(new DryRunProgress(
                            progressFrame.Phase, progressFrame.SourceFiles, progressFrame.DestinationFiles));
                        break;
                    case ErrorResponse error:
                        _poisoned = false;           // the server ends the stream after an error frame
                        return new IpcError(error.Code, error.Message);
                    default:
                        return new IpcError("IPC_UNEXPECTED_RESPONSE",
                            $"expected a dry-run chunk or completion, got {response!.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Result<DryRunCompletion, IpcError>.Canceled();
        }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException)
        {
            return new IpcError("IPC_TRANSPORT", ex.Message);
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return new IpcError("IPC_INTERNAL", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>The sink that reproduces the historical behaviour: append every record in receive
    /// order so the lists' indices stay global, then hand back one <see cref="DryRunReport"/>.
    /// Callers that can fold chunks incrementally should implement their own sink instead — this one
    /// holds the whole run.</summary>
    private sealed class ReportAssemblySink : IDryRunChunkSink
    {
        private readonly List<DryRunDirectory> _directories = [];
        private readonly List<DryRunFile> _sourceFiles = [];
        private readonly List<DryRunFile> _destinationFiles = [];
        private readonly List<DryRunOperation> _sourceOperations = [];
        private readonly List<DryRunOperation> _destinationOperations = [];

        public void OnChunk(DryRunChunkResponse chunk)
        {
            _directories.AddRange(chunk.Directories);
            _sourceFiles.AddRange(chunk.SourceFiles);
            _destinationFiles.AddRange(chunk.DestinationFiles);
            _sourceOperations.AddRange(chunk.SourceOperations);
            _destinationOperations.AddRange(chunk.DestinationOperations);
        }

        public DryRunReport ToReport(Guid profileId, DryRunCompletion completion) => new()
        {
            ProfileId = profileId,
            GeneratedAt = completion.GeneratedAt,
            Directories = _directories,
            SourceFiles = _sourceFiles,
            DestinationFiles = _destinationFiles,
            SourceOperations = _sourceOperations,
            DestinationOperations = _destinationOperations,
            Truncated = completion.Truncated,
            Space = completion.Space,
        };
    }

    /// <summary>Sends SubscribeEventsRequest; after the acknowledgment the connection is a
    /// one-way EngineEvent stream until disconnect (§3.2). Throws InvalidOperationException if
    /// the server refuses the subscription. Ends without error only on cancellation or a clean
    /// close at a frame boundary; a transport fault or unreadable event throws IOException so
    /// the failure reaches the consumer's logging boundary instead of ending the stream silently.</summary>
    public async IAsyncEnumerable<EngineEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        Result<OkResponse, IpcError> ack = await RequestAsync<OkResponse>(new SubscribeEventsRequest(), ct).ConfigureAwait(false);
        if (ack.IsCanceled)
            yield break;
        if (ack.TryGetError(out IpcError? error))
            throw new InvalidOperationException($"event subscription refused: {error.Code} — {error.Message}");

        while (!ct.IsCancellationRequested)
        {
            Result<byte[], string> frame = await IpcFrameCodec.ReadFrameAsync(_pipe, ct).ConfigureAwait(false);
            if (frame.IsCanceled)
                yield break;                       // shutdown
            if (frame.TryGetError(out string? readError))
            {
                if (readError == IpcFrameCodec.ConnectionClosedMessage)
                    yield break;                   // clean close at a frame boundary — stream over
                throw new System.IO.IOException($"event stream failed: {readError}");
            }
            frame.TryGetValue(out byte[]? bytes);

            Result<EngineEvent, string> parsed = IpcSerializer.DeserializeEvent(bytes!);
            if (parsed.TryGetError(out string? parseError))
                throw new System.IO.IOException($"event stream sent an unreadable event: {parseError}");
            parsed.TryGetValue(out EngineEvent? evt);
            yield return evt!;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The request gate is deliberately NOT disposed: a caller may legitimately dispose the
        // client while another request is parked in WaitAsync or releasing in its finally (the
        // gateway drops the shared client on transport death exactly this way). Disposing the
        // semaphore would turn that benign race into ObjectDisposedException inside the loser;
        // an undisposed SemaphoreSlim (no AvailableWaitHandle use) holds no unmanaged state.
        _poisoned = true;
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record IpcError(string Code, string Message);
