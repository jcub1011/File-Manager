using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Platform.Windows;

/// <summary>Named-pipe listener for the IPC server. PipeOptions.CurrentUserOnly on both ends
/// (the client sets it too, in Contracts' IpcClient) is the framework's per-user restriction —
/// it replaces a hand-rolled PipeSecurity ACL, is AOT-clean, and satisfies §3's "the pipe ACL
/// restricts to the current user".</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIpcEndpointProvider(ILogger<WindowsIpcEndpointProvider> logger) : IIpcEndpointProvider
{
    /// <summary>Explicit pipe buffers: the parameterless overload creates 0-byte buffers, and a
    /// zero-buffer named pipe makes every WriteFile block until the peer posts a read
    /// (rendezvous semantics) — needless latency coupling for a framed request/response protocol.</summary>
    private const int PipeBufferBytes = 64 * 1024;

    public async Task<Result<Stream, string>> AcceptAsync(CancellationToken ct = default)
    {
        NamedPipeServerStream pipe = new(
            ResolvePipeName(),
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: PipeBufferBytes,
            outBufferSize: PipeBufferBytes);
        try
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return Result<Stream, string>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not listen on the service pipe {PipeName}", ResolvePipeName());
            await pipe.DisposeAsync().ConfigureAwait(false);
            return $"could not listen on the service pipe: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Listening on the service pipe {PipeName} failed unexpectedly", ResolvePipeName());
            await pipe.DisposeAsync().ConfigureAwait(false);
            return $"could not listen on the service pipe: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Both ends of the pipe use Contracts' IpcEndpoint.Resolve derivation (including
    /// the FILEMANAGER_PIPE_NAME override) — one source of truth, so client and server can never
    /// derive different names (§4.11).</summary>
    internal static string ResolvePipeName() => IpcEndpoint.Resolve();
}
