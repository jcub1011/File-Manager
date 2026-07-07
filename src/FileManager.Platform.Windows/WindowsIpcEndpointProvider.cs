using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
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
public sealed class WindowsIpcEndpointProvider : IIpcEndpointProvider
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
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return $"could not listen on the service pipe: {ex.Message}";
        }
    }

    /// <summary>Deliberately duplicates Contracts' IpcEndpoint.Resolve derivation byte-for-byte,
    /// including the FILEMANAGER_PIPE_NAME override (§4.11 [flagged] — Contracts cannot see
    /// Core). The end-to-end pipe test pins the two together.</summary>
    internal static string ResolvePipeName()
    {
        string? overridden = Environment.GetEnvironmentVariable("FILEMANAGER_PIPE_NAME");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        char[] chars = Environment.UserName.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            bool legal = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!legal)
                chars[i] = '-';
        }
        return "filemanager-" + new string(chars);
    }
}
