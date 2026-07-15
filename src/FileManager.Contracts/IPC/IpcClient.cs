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

    private IpcClient(NamedPipeClientStream pipe) => _pipe = pipe;

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

        try
        {
            byte[] payload = IpcSerializer.SerializeRequest(request);
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
            return response! switch
            {
                ErrorResponse error => new IpcError(error.Code, error.Message),
                TResponse typed => typed,
                IpcResponse other => new IpcError("IPC_UNEXPECTED_RESPONSE",
                    $"expected {typeof(TResponse).Name}, got {other.GetType().Name}"),
            };
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
            _requestGate.Release();
        }
    }

    /// <summary>Sends a streaming dry-run request and reassembles the chunk frames
    /// (<see cref="DryRunChunkResponse"/>) into one <see cref="DryRunReport"/>, stopping at the
    /// <see cref="DryRunCompleteResponse"/> terminator. Because the report arrives as many small
    /// frames it is not bounded by the single-frame size cap. An ErrorResponse (e.g. PROFILE_NOT_FOUND,
    /// DRY_RUN_FAILED) and every transport fault surface as an <see cref="IpcError"/>; cancellation is
    /// a Canceled result, never a throw. Holds the request gate for the whole stream (§3.2).
    /// A Canceled result abandons the stream mid-flight: the service keeps writing the remaining
    /// chunk and completion frames, so those bytes are still queued on the pipe. This connection must
    /// therefore be discarded after a cancellation (as callers do — one connection per dry run);
    /// reusing it for another request would read the stale frames and desync the protocol.</summary>
    public async Task<Result<DryRunReport, IpcError>> DryRunStreamAsync(
        DryRunStreamRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<DryRunReport, IpcError>.Canceled();
        }

        try
        {
            byte[] payload = IpcSerializer.SerializeRequest(request);
            Result writeResult = await IpcFrameCodec.WriteFrameAsync(_pipe, payload, ct).ConfigureAwait(false);
            if (writeResult.IsCanceled)
                return Result<DryRunReport, IpcError>.Canceled();
            if (writeResult.TryGetError(out string? writeError))
                return new IpcError("IPC_TRANSPORT", writeError);

            // Appended in receive order so the lists' indices stay global — an op's
            // SourceIndex/SubjectIndex is a position into the fully assembled file lists, and a
            // file/op's DirIndex/RootDirIndex is a position into the assembled directory table.
            List<DryRunDirectory> directories = [];
            List<DryRunFile> sourceFiles = [];
            List<DryRunFile> destinationFiles = [];
            List<DryRunOperation> sourceOperations = [];
            List<DryRunOperation> destinationOperations = [];
            while (true)
            {
                Result<byte[], string> frame = await IpcFrameCodec.ReadFrameAsync(_pipe, ct).ConfigureAwait(false);
                if (frame.IsCanceled)
                    return Result<DryRunReport, IpcError>.Canceled();
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
                            if (dir.ParentIndex < -1 || dir.ParentIndex >= directories.Count)
                                return new IpcError("IPC_MALFORMED",
                                    $"the service sent a directory entry ('{dir.Name}') whose ParentIndex {dir.ParentIndex} does not precede it in the table");
                            directories.Add(dir);
                        }
                        sourceFiles.AddRange(chunk.SourceFiles);
                        destinationFiles.AddRange(chunk.DestinationFiles);
                        sourceOperations.AddRange(chunk.SourceOperations);
                        destinationOperations.AddRange(chunk.DestinationOperations);
                        break;
                    case DryRunCompleteResponse complete:
                        // TEMPORARY (directory-table migration, stage B): reconstruct the stringy
                        // report until DryRunReport itself carries the normalized shape (stage C),
                        // at which point the assembled lists return directly.
                        string[] dirPaths = DryRunDirectoryTable.Materialize(directories);
                        return new DryRunReport
                        {
                            ProfileId = request.ProfileId,
                            GeneratedAt = complete.GeneratedAt,
                            SourceFiles = sourceFiles.ConvertAll(f => ToPhysicalFile(f, dirPaths)),
                            DestinationFiles = destinationFiles.ConvertAll(f => ToPhysicalFile(f, dirPaths)),
                            SourceOperations = sourceOperations.ConvertAll(o => ToOperation(o, dirPaths)),
                            DestinationOperations = destinationOperations.ConvertAll(o => ToOperation(o, dirPaths)),
                            Truncated = complete.Truncated,
                            Space = complete.Space,
                        };
                    case ErrorResponse error:
                        return new IpcError(error.Code, error.Message);
                    default:
                        return new IpcError("IPC_UNEXPECTED_RESPONSE",
                            $"expected a dry-run chunk or completion, got {response!.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Result<DryRunReport, IpcError>.Canceled();
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
            _requestGate.Release();
        }
    }

    // TEMPORARY (directory-table migration, stage B): the stringy bridge from the normalized wire
    // shape back to the current DryRunReport. Deleted when the report itself goes normalized.
    private static PhysicalFile ToPhysicalFile(DryRunFile file, string[] dirPaths) => new()
    {
        Path = System.IO.Path.Join(dirPaths[file.DirIndex], file.FileName),
        Root = dirPaths[file.RootDirIndex],
        Length = file.Length,
        LastWritten = file.LastWritten,
        IsReparsePoint = file.IsReparsePoint,
    };

    private static VirtualFileOperation ToOperation(DryRunOperation op, string[] dirPaths) => new()
    {
        Path = System.IO.Path.Join(dirPaths[op.DirIndex], op.FileName),
        Root = dirPaths[op.RootDirIndex],
        Kind = op.Kind,
        SourceIndex = op.SourceIndex,
        SubjectIndex = op.SubjectIndex,
        SourceDisposition = op.SourceDisposition,
        Detail = op.Detail,
    };

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
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }
}

public sealed record IpcError(string Code, string Message);
