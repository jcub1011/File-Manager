using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    DestinationProjector destinationProjector, IVolumeInfoProvider volumes, EngineConfig config,
    ISettingsProvider settings)
    : IIpcStreamingRequestHandler
{
    /// <summary>Destination sweep entries per streamed frame. Each entry is small (a physical file +
    /// its operation) so a few thousand keep each frame well under the 16 MiB cap.</summary>
    private const int DestinationChunkSize = 4096;

    /// <summary>Spacing of the interleaved <see cref="DryRunProgressResponse"/> frames. The handler
    /// samples the shared counters on this timer while a discovery phase is in flight, so throttling
    /// is structural (at most ~10 frames/sec regardless of file rate) and the hot per-file paths pay
    /// only an uncontended increment.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

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
        // Destination files seen across the file phase — the running offset applied to the sweep's
        // SubjectIndex values so they stay global once the sweep frames are appended after the file
        // frames (the client concatenates chunks in receive order).
        int destinationCount = 0;
        bool truncated = false;
        // Byte/space projection, folded from the same chunks as they stream by (no extra buffering).
        // A chunk's op indices are global; passing the running (emitted, destinationCount) as bases
        // lets the estimator resolve them against the chunk's own file lists.
        var estimator = new DryRunSpaceEstimator(volumes, config.PreflightSafetyMarginBytes);
        bool stageOverwrites = profile.Policies.OverwriteHandling == OverwriteHandling.StageOverwrites;
        // Phase attribution for the whole streamed run (the engine logs its own scan/evaluate split):
        // total wall time, the destination sweep, and the estimator's cumulative share. The estimator
        // watch is started/stopped around each fold so it accumulates only its own time.
        Stopwatch totalWatch = Stopwatch.StartNew();
        Stopwatch estimatorWatch = new();
        // Accumulate only the (small) set of destination paths a source writes to (every destination
        // operation's resulting path) as chunks stream by — NOT the file objects — so the destination
        // sweep below can identify orphans without retaining the whole report in memory.
        HashSet<NormalizedPath> survivors = [];
        // One converter for the whole stream: it owns the report's directory-index space across the
        // file phase AND the sweep, so each outgoing chunk carries exactly its first-referenced
        // directory entries with globally valid indices.
        WireChunkConverter converter = new();
        // Live discovery counters, incremented by the engine's scan pump (sources) and the sweep's
        // walkers (destinations); sampled on ProgressInterval below so the interleaved progress
        // frames stay throttled no matter how fast files are found.
        DryRunProgressCounters progressCounters = new();
        await using (IAsyncEnumerator<Result<DryRunChunk, string>> chunks = engine
            .SimulateStreamAsync(typed.ProfileId, typed.ScopePath, progressCounters, ct)
            .GetAsyncEnumerator(ct))
        {
            // The engine's whole scan+evaluate pipeline runs inside the FIRST MoveNextAsync (no chunk
            // exists before the full evaluated set is sorted), so scan progress is merged into the
            // stream by polling the counters while each advance is pending. Only a changed count is
            // worth a frame — a stalled scan goes quiet instead of repeating itself.
            Task<bool> moveNext = chunks.MoveNextAsync().AsTask();
            long lastSources = -1;
            while (true)
            {
                while (!moveNext.IsCompleted)
                {
                    await Task.WhenAny(moveNext, Task.Delay(ProgressInterval, ct)).ConfigureAwait(false);
                    if (!moveNext.IsCompleted && progressCounters.Sources != lastSources)
                    {
                        lastSources = progressCounters.Sources;
                        yield return new DryRunProgressResponse
                        {
                            Phase = DryRunProgressPhase.ScanningSources,
                            SourceFiles = lastSources,
                            DestinationFiles = destinationCount,
                        };
                    }
                }
                // Propagates engine faults and cancellation exactly as the plain foreach did.
                if (!await moveNext.ConfigureAwait(false))
                    break;
                Result<DryRunChunk, string> chunk = chunks.Current;

                if (chunk.TryGetError(out string? error))
                {
                    yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = error };
                    yield break;
                }
                chunk.TryGetValue(out DryRunChunk? slice);
                // The engine sets ScanTruncated once its candidate scan is cut short. OR it in BEFORE the
                // sweep so a prefix-only survivor set never drives a (bogus) Mirror-deletion preview.
                truncated |= slice!.ScanTruncated;
                // Fold the stringy slice into the estimator and survivor set BEFORE converting: both key
                // on absolute path/root strings, so they consume the engine's shape, never the wire's.
                estimatorWatch.Start();
                estimator.Accumulate(
                    slice.SourceFiles, slice.DestinationFiles, slice.SourceOperations, slice.DestinationOperations,
                    emitted, destinationCount, stageOverwrites);
                estimatorWatch.Stop();
                DestinationProjector.AccumulateSurvivors(survivors, slice.DestinationOperations);
                yield return converter.Convert(slice);
                destinationCount += slice.DestinationFiles.Count;

                emitted += slice.SourceFiles.Count;
                if (emitted >= MaxStreamedFiles)
                {
                    logger.LogWarning(
                        "Dry-run (stream) for profile {ProfileId} hit the {Cap:N0}-file safety bound; report truncated",
                        typed.ProfileId, MaxStreamedFiles);
                    truncated = true;
                    break;
                }

                moveNext = chunks.MoveNextAsync().AsTask();
            }
        }

        // Phase 3: sweep the destination roots for pre-existing/orphan files. Suppressed when
        // truncated (survivor set incomplete → any orphan call is untrustworthy). The sweep's ops
        // reference their own files 0-based; offset both the file positions and the ops' SubjectIndex
        // by the file-phase destination count so indices stay global. Chunked so each frame stays
        // under the cap.
        // Bound the sweep by the same overall file budget the source phase uses, so a target root
        // with millions of pre-existing files can't buffer an unbounded op-per-file set service-side.
        long engineMs = totalWatch.ElapsedMilliseconds;
        int sweepBudget = Math.Max(0, MaxStreamedFiles - destinationCount);
        Stopwatch sweepWatch = Stopwatch.StartNew();
        // Resolve a Manual worker pin (profile, or Inherit → global) exactly as the batched engine
        // does; Automatic leaves it null so the projector auto-scales to the target medium.
        int? manualWorkers = DryRunConcurrency.ResolveManualWorkers(profile, settings.Current);
        // The sweep is a blocking call (its walkers join via Task.WaitAll), so hop it to the pool and
        // keep the stream alive with throttled progress frames while it runs — same polling shape as
        // the scan phase above. Live counts ride on top of the file phase's destination total so the
        // figure the user watches never goes backwards.
        Task<DestinationSweepResult> sweepTask = Task.Run(
            () => destinationProjector.Sweep(
                profile, survivors, truncated, manualWorkers, ct, sweepBudget, progressCounters));
        long lastDestinations = -1;
        while (!sweepTask.IsCompleted)
        {
            await Task.WhenAny(sweepTask, Task.Delay(ProgressInterval, ct)).ConfigureAwait(false);
            if (!sweepTask.IsCompleted && progressCounters.Destinations != lastDestinations)
            {
                lastDestinations = progressCounters.Destinations;
                yield return new DryRunProgressResponse
                {
                    Phase = DryRunProgressPhase.SweepingDestinations,
                    SourceFiles = progressCounters.Sources,
                    DestinationFiles = destinationCount + lastDestinations,
                };
            }
        }
        DestinationSweepResult sweep = await sweepTask.ConfigureAwait(false);
        sweepWatch.Stop();
        if (sweep.Truncated)
        {
            logger.LogWarning(
                "Dry-run (stream) destination sweep for profile {ProfileId} hit the {Cap:N0}-entry bound; report truncated",
                typed.ProfileId, MaxStreamedFiles);
            truncated = true;
        }
        // The sweep's ops index their own file list directly (Ops[i].SubjectIndex == i), so feed it
        // as a standalone chunk with both bases 0. Deleted orphans free space; Untouched/Unknown don't.
        estimatorWatch.Start();
        estimator.Accumulate([], sweep.Files, [], sweep.Ops, 0, 0, stageOverwrites);
        estimatorWatch.Stop();
        // Discovery is done — one unconditional frame flips the client to its final phase, covering
        // sweep-chunk transfer, client reassembly, and the UI's report projection.
        yield return new DryRunProgressResponse
        {
            Phase = DryRunProgressPhase.BuildingLists,
            SourceFiles = progressCounters.Sources,
            DestinationFiles = destinationCount + sweep.Files.Count,
        };
        for (int start = 0; start < sweep.Files.Count; start += DestinationChunkSize)
        {
            int count = Math.Min(DestinationChunkSize, sweep.Files.Count - start);
            var sliceFiles = new List<PhysicalFile>(count);
            var sliceOps = new List<VirtualFileOperation>(count);
            for (int i = 0; i < count; i++)
            {
                sliceFiles.Add(sweep.Files[start + i]);
                sliceOps.Add(sweep.Ops[start + i] with { SubjectIndex = destinationCount + start + i });
            }
            yield return converter.Convert([], sliceFiles, [], sliceOps);
        }

        // Skip the projection on a truncated report — totals over a partial graph would be unsound.
        estimatorWatch.Start();
        SpaceProjection? space = truncated ? null : estimator.Finalize(config.MaxWorkers);
        estimatorWatch.Stop();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Dry-run stream timings for profile {ProfileId}: total {TotalMs}ms " +
                "(engine stream {EngineMs}ms, destination sweep {SweepMs}ms [walk {WalkMs}ms, merge {MergeMs}ms], " +
                "space estimator {EstimatorMs}ms), " +
                "{SourceCount} source files, {DestCount} destination files (+{SweepCount} swept)",
                typed.ProfileId, totalWatch.ElapsedMilliseconds, engineMs, sweepWatch.ElapsedMilliseconds,
                sweep.WalkMs, sweep.MergeMs, estimatorWatch.ElapsedMilliseconds, emitted, destinationCount,
                sweep.Files.Count);
        yield return new DryRunCompleteResponse { GeneratedAt = time.GetUtcNow(), Truncated = truncated, Space = space };
    }

    /// <summary>Converts the engine's stringy slices to the normalized wire shape. One instance owns
    /// the stream's whole directory-index space — the file-phase chunks and the handler-emitted sweep
    /// chunks extend the same table, so every index stays a valid position into the client's
    /// concatenated <c>Directories</c> list. Each outgoing chunk carries exactly the directory
    /// entries it references first (<see cref="DryRunDirectoryTableBuilder.FlushNew"/>), before the
    /// files/ops that use them.</summary>
    private sealed class WireChunkConverter
    {
        private readonly DryRunDirectoryTableBuilder _dirs = new();

        public DryRunChunkResponse Convert(DryRunChunk slice) =>
            Convert(slice.SourceFiles, slice.DestinationFiles, slice.SourceOperations, slice.DestinationOperations);

        public DryRunChunkResponse Convert(
            IReadOnlyList<PhysicalFile> sourceFiles, IReadOnlyList<PhysicalFile> destinationFiles,
            IReadOnlyList<VirtualFileOperation> sourceOps, IReadOnlyList<VirtualFileOperation> destinationOps)
        {
            List<DryRunFile> wireSourceFiles = new(sourceFiles.Count);
            foreach (PhysicalFile f in sourceFiles)
                wireSourceFiles.Add(_dirs.Convert(f));
            List<DryRunFile> wireDestinationFiles = new(destinationFiles.Count);
            foreach (PhysicalFile f in destinationFiles)
                wireDestinationFiles.Add(_dirs.Convert(f));
            List<DryRunOperation> wireSourceOps = new(sourceOps.Count);
            foreach (VirtualFileOperation o in sourceOps)
                wireSourceOps.Add(_dirs.Convert(o));
            List<DryRunOperation> wireDestinationOps = new(destinationOps.Count);
            foreach (VirtualFileOperation o in destinationOps)
                wireDestinationOps.Add(_dirs.Convert(o));

            return new DryRunChunkResponse
            {
                Directories = _dirs.FlushNew(),
                SourceFiles = wireSourceFiles,
                DestinationFiles = wireDestinationFiles,
                SourceOperations = wireSourceOps,
                DestinationOperations = wireDestinationOps,
            };
        }
    }
}
