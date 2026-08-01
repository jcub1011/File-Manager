using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
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
    IEngineEventBus eventBus, IMemoryTrimCoordinator trimCoordinator)
    : IIpcStreamingRequestHandler
{
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
        // An inline draft (unsaved edits) is previewed directly; otherwise resolve the persisted
        // catalog. PROFILE_NOT_FOUND is only reachable on the non-inline path. Accepting a
        // client-supplied profile grants no new authority: the dry run is read-only (I-DRYRUN-RO)
        // and the service runs at the same trust level as the local UI over the local IPC channel.
        var profile = typed.InlineProfile ?? catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
        {
            logger.LogDebug("DryRunStream: requested profile {ProfileId} not found", typed.ProfileId);
            yield return new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
            yield break;
        }

        // Marks the run as in flight (which suppresses any memory trim while it is), and on disposal
        // arms the debounce that eventually returns this run's peak to the OS. Disposal happens when
        // the async iterator is torn down, so it covers the early yield-break paths and cancellation
        // too, not just the happy path.
        using IMemoryTrimScope trimScope = trimCoordinator.BeginOperation();

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

        // The one place a chunk becomes a frame, shared by BOTH phases (the file phase and the
        // destination sweep now speak the same DryRunChunk currency, so there is no second copy of
        // this arithmetic to drift). Folds the stringy slice into the estimator — and, for the file
        // phase, the survivor set — BEFORE converting, because both key on absolute path/root strings
        // and must consume the engine's shape, never the wire's. Advances the global index bases and
        // returns the frame: a local function cannot yield on its caller's behalf.
        DryRunChunkResponse ConsumeSlice(DryRunChunk slice, bool collectSurvivors)
        {
            estimatorWatch.Start();
            estimator.Accumulate(
                slice.SourceFiles, slice.DestinationFiles, slice.SourceOperations, slice.DestinationOperations,
                emitted, destinationCount, stageOverwrites);
            estimatorWatch.Stop();
            // Survivors come from the FILE phase only — they are what the sweep tests each pre-existing
            // file against. Feeding the sweep's own output back in could never change an answer (the
            // sweep is finished with the set by then) and would grow the very structure streaming
            // exists to keep small: at 357k swept paths that set alone is ~80 MB.
            if (collectSurvivors)
                DestinationProjector.AccumulateSurvivors(survivors, slice.DestinationOperations);
            DryRunChunkResponse response = converter.Convert(slice);
            destinationCount += slice.DestinationFiles.Count;
            emitted += slice.SourceFiles.Count;
            // Updated per chunk rather than once at the end so a run that is cancelled or errors out
            // half way still reports the memory it actually churned.
            trimScope.Units = emitted + destinationCount;
            return response;
        }

        await using (IAsyncEnumerator<Result<DryRunChunk, string>> chunks = engine
            .SimulateStreamAsync(profile, typed.ScopePath, progressCounters, ct)
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
                    // Once ct fires, Task.Delay(…, ct) completes instantly and this poll would spin
                    // a core until the engine finishes unwinding — just await the phase task (it
                    // observes ct) instead of polling for progress nobody will see.
                    if (ct.IsCancellationRequested)
                        break;
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
                yield return ConsumeSlice(slice, collectSurvivors: true);

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

        // Phase 3: sweep the destination roots for pre-existing/orphan files, STREAMED. Suppressed when
        // truncated (survivor set incomplete → any orphan call is untrustworthy).
        //
        // The sweep now emits the same byte-budgeted DryRunChunks the file phase does, with global
        // indices already applied, so this phase is the file phase's loop with a different source. What
        // it replaces: a blocking Sweep() that built one PhysicalFile + one VirtualFileOperation + one
        // path string for EVERY pre-existing file under every target root, sorted them, and held the lot
        // while the handler copied it into frames (~145 MB live plus ~23 MB of copy churn at 357k
        // entries). Memory is now one chunk regardless of how large the target tree is.
        //
        // Bound the sweep by the same overall file budget the source phase uses, so a target root
        // with millions of pre-existing files can't stream an unbounded set to the client.
        long engineMs = totalWatch.ElapsedMilliseconds;
        int sweepBudget = Math.Max(0, MaxStreamedFiles - destinationCount);
        Stopwatch sweepWatch = Stopwatch.StartNew();
        int sweptCount = 0;
        // AdditiveArchive can skip the sweep when the profile opts out (ScanDestination = false);
        // Mirror always sweeps because the sweep is its only source of Deleted-orphan previews.
        // SweepStreamAsync yields nothing when truncated, but short-circuit here too so the (non-trivial)
        // root resolution and scan-session setup are skipped as well.
        if (profile.EffectiveScanDestination && !truncated)
        {
            // The base for the sweep's global SubjectIndex values: every destination file the file
            // phase already emitted. Fixed for the whole sweep — the projector adds its own running
            // position on top.
            int sweepIndexBase = destinationCount;
            await using IAsyncEnumerator<Result<DryRunChunk, string>> sweepChunks = destinationProjector
                .SweepStreamAsync(
                    profile, survivors, truncated, sweepBudget, sweepIndexBase,
                    DryRunEngine.WireChunkByteBudget, progressCounters, ct)
                .GetAsyncEnumerator(ct);

            Task<bool> sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
            long lastDestinations = -1;
            while (true)
            {
                // Same throttled-progress poll as the scan phase: the walk can spend seconds between
                // chunks on a big tree, and the live count is the only sign of life. Data frames now
                // interleave with these instead of arriving in one burst after a blocking call.
                while (!sweepMoveNext.IsCompleted)
                {
                    // Anti-spin guard: a cancelled token makes Task.Delay(…, ct) complete instantly.
                    if (ct.IsCancellationRequested)
                        break;
                    await Task.WhenAny(sweepMoveNext, Task.Delay(ProgressInterval, ct)).ConfigureAwait(false);
                    if (!sweepMoveNext.IsCompleted && progressCounters.Destinations != lastDestinations)
                    {
                        lastDestinations = progressCounters.Destinations;
                        yield return new DryRunProgressResponse
                        {
                            Phase = DryRunProgressPhase.SweepingDestinations,
                            SourceFiles = progressCounters.Sources,
                            DestinationFiles = sweepIndexBase + lastDestinations,
                        };
                    }
                }
                if (!await sweepMoveNext.ConfigureAwait(false))
                    break;
                Result<DryRunChunk, string> sweepChunk = sweepChunks.Current;

                if (sweepChunk.TryGetError(out string? sweepError))
                {
                    yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = sweepError };
                    yield break;
                }
                sweepChunk.TryGetValue(out DryRunChunk? sweepSlice);
                if (sweepSlice!.SweepCapped)
                {
                    logger.LogWarning(
                        "Dry-run (stream) destination sweep for profile {ProfileId} hit the {Cap:N0}-entry bound; report truncated",
                        typed.ProfileId, MaxStreamedFiles);
                    truncated = true;
                }
                sweptCount += sweepSlice.DestinationFiles.Count;
                // The capped marker is an empty chunk; don't put an empty frame on the wire for it.
                if (sweepSlice.DestinationFiles.Count == 0)
                {
                    sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
                    continue;
                }
                yield return ConsumeSlice(sweepSlice, collectSurvivors: false);

                sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
            }
        }
        sweepWatch.Stop();
        // Discovery is done — one unconditional frame flips the client to its final phase, covering
        // client reassembly and the UI's report projection.
        yield return new DryRunProgressResponse
        {
            Phase = DryRunProgressPhase.BuildingLists,
            SourceFiles = progressCounters.Sources,
            DestinationFiles = destinationCount,
        };

        // Skip the projection on a truncated report — totals over a partial graph would be unsound.
        estimatorWatch.Start();
        SpaceProjection? space = truncated ? null : estimator.Finalize(config.MaxWorkers);
        estimatorWatch.Stop();
        if (logger.IsEnabled(LogLevel.Information))
        {
            // Three-way memory split. NOTE THE SAMPLE POINT: this runs at the end of the run but
            // BEFORE the iterator tears down, so the sweep result, the space estimator, the survivor
            // set and the wire directory table are all still rooted. It is therefore a NEAR-PEAK
            // reading, not the post-run residual — do not quote it as "what the service settles at".
            // The residual needs a sample after this method's frame is gone.
            //
            // These are NOT interchangeable; the whole point of logging all four is that they answer
            // different questions:
            //   managed  — GC.GetTotalMemory(false), the managed heap as the GC last accounted it.
            //   heap     — HeapSizeBytes, live+garbage bytes the GC currently tracks.
            //   committed— TotalCommittedBytes, what the GC has committed from the OS.
            //   private  — the process's private commit, i.e. what Task Manager and a user report.
            // committed >> heap means the residual is committed-but-free GC heap (a trim/GC-config
            // problem); heap >> idle means something is still retained (a lifetime problem). Do not
            // force a collection here — GC.GetTotalMemory(true) would perturb the very number being
            // measured and add a blocking gen2 to every run.
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            long managedBytes = GC.GetTotalMemory(false);
            long privateBytes;
            using (Process self = Process.GetCurrentProcess())
                privateBytes = self.PrivateMemorySize64;
            logger.LogInformation(
                "Dry-run stream timings for profile {ProfileId}: total {TotalMs}ms " +
                "(engine stream {EngineMs}ms, destination sweep {SweepMs}ms, space estimator {EstimatorMs}ms), " +
                "{SourceCount} source files, {DestCount} destination files (+{SweepCount} swept); " +
                "memory managed {ManagedMb}MB, GC heap {HeapMb}MB, GC committed {CommittedMb}MB, " +
                "process private {PrivateMb}MB",
                typed.ProfileId, totalWatch.ElapsedMilliseconds, engineMs, sweepWatch.ElapsedMilliseconds,
                estimatorWatch.ElapsedMilliseconds, emitted, destinationCount, sweptCount,
                managedBytes >> 20, gcInfo.HeapSizeBytes >> 20, gcInfo.TotalCommittedBytes >> 20,
                privateBytes >> 20);
        }
        // Directories the walk could not open were downgraded to warnings by SourceScanner and dropped
        // by the engine's pump, so the report below looks complete. DryRunCompleteResponse is frozen and
        // has no skipped count, so the partiality rides the warning channel the UI already renders in
        // its notice bar — the same place a truncated report is called out. Without it the user
        // approves a plan (possibly with OnSuccess = PermanentDelete) over a partial tree.
        if (progressCounters.Skipped > 0)
            eventBus.Publish(new EngineWarningEvent
            {
                AtUtc = time.GetUtcNow(),
                Message = $"{progressCounters.Skipped} item(s) under the scanned source(s) could not be read and are missing from this preview (see the service log for details).",
            });
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
            IReadOnlyList<IPhysicalFileView> sourceFiles, IReadOnlyList<IPhysicalFileView> destinationFiles,
            IReadOnlyList<IFileOperationView> sourceOps, IReadOnlyList<IFileOperationView> destinationOps)
        {
            List<DryRunFile> wireSourceFiles = new(sourceFiles.Count);
            foreach (IPhysicalFileView f in sourceFiles)
                wireSourceFiles.Add(_dirs.Convert(f));
            List<DryRunFile> wireDestinationFiles = new(destinationFiles.Count);
            foreach (IPhysicalFileView f in destinationFiles)
                wireDestinationFiles.Add(_dirs.Convert(f));
            List<DryRunOperation> wireSourceOps = new(sourceOps.Count);
            foreach (IFileOperationView o in sourceOps)
                wireSourceOps.Add(_dirs.Convert(o));
            List<DryRunOperation> wireDestinationOps = new(destinationOps.Count);
            foreach (IFileOperationView o in destinationOps)
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
