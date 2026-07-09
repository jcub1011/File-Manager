using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.DryRun;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Streaming counterpart to <see cref="DryRunHandler"/>: the report comes back as a
/// sequence of <see cref="DryRunChunkResponse"/> frames terminated by a single
/// <see cref="DryRunCompleteResponse"/>, so a report is no longer bounded by the single-frame size
/// cap. A profile that does not exist, or a setup/scan failure, is a single <see cref="ErrorResponse"/>
/// frame (matching DryRunHandler's error codes).</summary>
public sealed class DryRunStreamHandler(
    ILogger<DryRunStreamHandler> logger, IDryRunEngine engine, IProfileCatalog catalog, TimeProvider time)
    : IIpcStreamingRequestHandler
{
    /// <summary>Safety bound on a single streamed report. Streaming removes the frame-cap ceiling,
    /// but not the good sense of an upper limit — a pathological scan is truncated here (surfaced via
    /// <see cref="DryRunCompleteResponse.Truncated"/>) rather than streaming unboundedly. Far above
    /// the old ~50k cap.</summary>
    internal const int MaxStreamedFiles = 500_000;

    public string RequestType => IpcRequestTypes.DryRunStream;

    /// <summary>Never invoked — the server routes streaming handlers to <see cref="HandleStreamAsync"/>.</summary>
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(DryRunStreamHandler)} is a streaming handler; the server must call {nameof(HandleStreamAsync)}");

    public async IAsyncEnumerable<IpcResponse> HandleStreamAsync(
        IpcRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typed = (DryRunStreamRequest)request;
        if (catalog.All.All(p => p.Id != typed.ProfileId))
        {
            logger.LogDebug("DryRunStream: requested profile {ProfileId} not found", typed.ProfileId);
            yield return new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
            yield break;
        }

        int emitted = 0;
        bool truncated = false;
        await foreach (Result<IReadOnlyList<DryRunFileResult>, string> chunk in
            engine.SimulateStreamAsync(typed.ProfileId, typed.ScopePath, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (chunk.TryGetError(out string? error))
            {
                yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = error };
                yield break;
            }
            chunk.TryGetValue(out IReadOnlyList<DryRunFileResult>? files);
            yield return new DryRunChunkResponse { Files = files! };

            emitted += files!.Count;
            if (emitted >= MaxStreamedFiles)
            {
                logger.LogWarning(
                    "Dry-run (stream) for profile {ProfileId} hit the {Cap:N0}-file safety bound; report truncated",
                    typed.ProfileId, MaxStreamedFiles);
                truncated = true;
                break;
            }
        }

        yield return new DryRunCompleteResponse { GeneratedAt = time.GetUtcNow(), Truncated = truncated };
    }
}
