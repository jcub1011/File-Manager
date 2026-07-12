using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
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
    ILogger<DryRunStreamHandler> logger, IDryRunEngine engine, IProfileCatalog catalog, TimeProvider time,
    DestinationProjector destinationProjector)
    : IIpcStreamingRequestHandler
{
    /// <summary>Destination entries per streamed frame. Entries are small (a path or two + a short
    /// disposition) so a few thousand keep each frame well under the 16 MiB cap.</summary>
    private const int DestinationChunkSize = 4096;

    /// <summary>Bound on the files a single streamed report forwards to the client, surfaced via
    /// <see cref="DryRunCompleteResponse.Truncated"/>. This is the user-visible half of the safety
    /// bound; the engine independently caps its candidate buffer at the same
    /// <see cref="DryRunEngine.MaxStreamedFiles"/> so memory and evaluation are bounded even before
    /// the first chunk (see <see cref="DryRunEngine.MaxScannedCandidates"/>). Against the real engine
    /// this emitted cap is a redundant backstop — the engine's candidate cap fires first (filtering
    /// only ever reduces the count), so this rarely trips. It is kept for defense in depth against a
    /// future engine that streams differently, and is independently testable via the seam below.
    /// Test seam: shrunk so truncation is reachable without half a million files.</summary>
    internal int MaxStreamedFiles { get; init; } = DryRunEngine.MaxStreamedFiles;

    public string RequestType => IpcRequestTypes.DryRunStream;

    /// <summary>Never invoked — the server routes streaming handlers to <see cref="HandleStreamAsync"/>.</summary>
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(DryRunStreamHandler)} is a streaming handler; the server must call {nameof(HandleStreamAsync)}");

    public async IAsyncEnumerable<IpcResponse> HandleStreamAsync(
        IpcRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typed = (DryRunStreamRequest)request;
        var profile = catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
        {
            logger.LogDebug("DryRunStream: requested profile {ProfileId} not found", typed.ProfileId);
            yield return new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
            yield break;
        }

        int emitted = 0;
        bool truncated = false;
        // Accumulate only the (small) set of destination paths a source writes to as chunks stream
        // by — NOT the file objects — so the destination sweep below can identify orphans without
        // retaining the whole report in memory.
        HashSet<NormalizedPath> survivors = [];
        await foreach (Result<IReadOnlyList<DryRunFileResult>, string> chunk in
            engine.SimulateStreamAsync(typed.ProfileId, typed.ScopePath, ct).ConfigureAwait(false))
        {
            if (chunk.TryGetError(out string? error))
            {
                yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = error };
                yield break;
            }
            chunk.TryGetValue(out IReadOnlyList<DryRunFileResult>? files);
            yield return new DryRunChunkResponse { Files = files! };
            DestinationProjector.AccumulateSurvivors(survivors, files!);

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

        // Phase 3: sweep the destination roots for pre-existing/orphan files. Suppressed when
        // truncated (survivor set incomplete → any orphan call is untrustworthy). Chunked like the
        // file frames so each stays under the frame cap.
        IReadOnlyList<DryRunDestinationEntry> destinations =
            destinationProjector.Sweep(profile, survivors, truncated, ct);
        for (int start = 0; start < destinations.Count; start += DestinationChunkSize)
        {
            int count = Math.Min(DestinationChunkSize, destinations.Count - start);
            var slice = new List<DryRunDestinationEntry>(count);
            for (int i = 0; i < count; i++)
                slice.Add(destinations[start + i]);
            yield return new DryRunDestinationChunkResponse { Entries = slice };
        }

        yield return new DryRunCompleteResponse { GeneratedAt = time.GetUtcNow(), Truncated = truncated };
    }
}
