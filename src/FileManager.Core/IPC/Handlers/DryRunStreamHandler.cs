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

    /// <summary>Test seam. Production (the IPC server) serializes each frame before requesting the
    /// next, so the converter reuses the previous frame's wire DTOs (see
    /// <see cref="WireChunkConverter"/>). An in-proc consumer that BUFFERS responses across advances
    /// — which several handler tests do — would observe recycled records, so those tests construct
    /// the handler with this off. The byte-identical on/off round-trip test is the guard that keeps
    /// the recycling path equivalent.</summary>
    internal bool RecycleWireRecords { get; init; } = true;

    public string RequestType => IpcRequestTypes.DryRunStream;

    /// <summary>Never invoked — the server routes streaming handlers to <see cref="HandleStreamAsync"/>.</summary>
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(DryRunStreamHandler)} is a streaming handler; the server must call {nameof(HandleStreamAsync)}");

    /// <summary><b>Ownership contract:</b> a yielded <see cref="DryRunChunkResponse"/>'s wire records
    /// are valid only until the consumer requests the NEXT response — advancing the enumerator is
    /// what hands them back to be refilled (see <see cref="WireChunkConverter"/>, and the identical
    /// contract <c>DestinationProjector.SweepStreamAsync</c> puts on the carriers underneath). Serialize,
    /// copy, or otherwise finish with each frame before advancing; never buffer the records themselves
    /// across advances. <c>IpcServer.ServeStreamAsync</c> — the production consumer —
    /// writes each frame to the pipe before its next <c>MoveNextAsync</c>, which is what makes this
    /// safe; a consumer that cannot honor it constructs the handler with
    /// <see cref="RecycleWireRecords"/> off (buffering a recycled frame is silent, not an exception:
    /// every file name reads back empty and every Detail null).</summary>
    public async IAsyncEnumerable<IpcResponse> HandleStreamAsync(
        IpcRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typed = (DryRunStreamRequest)request;
        // Total managed bytes allocated by the process before this run, so the end-of-run log line
        // can report the run's allocation CHURN (rate), not just the heap snapshot (retention). The
        // two answer different questions: churn is what the GC must absorb during the burst and is
        // what drives in-run peak commit; the snapshot is what's left. Process-wide is acceptable
        // because the service serializes dry runs behind the single-instance mutex.
        long allocatedBefore = GC.GetTotalAllocatedBytes();
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

        // The run's own cancellation authority, cancelled ONLY when this iterator is torn down while a
        // phase advance is still in flight (see ObserveAbandonedAdvanceAsync). ct is the SERVER's
        // token — it fires on shutdown, never on client disconnect — so when the server abandons this
        // stream (a frame write failed mid-run) nothing else would ever stop the engine pipeline or
        // the sweep's producer. Both phase enumerators run on this token so that teardown can.
        using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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
        // sweep below can identify orphans without retaining the whole report in memory. SurvivorSet
        // rather than a HashSet<NormalizedPath> so the sweep can probe it by span, without a per-file
        // path string.
        SurvivorSet survivors = new();
        // One converter for the whole stream: it owns the report's directory-index space across the
        // file phase AND the sweep, so each outgoing chunk carries exactly its first-referenced
        // directory entries with globally valid indices.
        WireChunkConverter converter = new(RecycleWireRecords);
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
            .SimulateStreamAsync(profile, typed.ScopePath, progressCounters, runCts.Token)
            .GetAsyncEnumerator(runCts.Token))
        {
            // The engine's whole scan+evaluate pipeline runs inside the FIRST MoveNextAsync (no chunk
            // exists before the full evaluated set is sorted), so scan progress is merged into the
            // stream by polling the counters while each advance is pending. Only a changed count is
            // worth a frame — a stalled scan goes quiet instead of repeating itself.
            Task<bool> moveNext = chunks.MoveNextAsync().AsTask();
            try
            {
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
            finally
            {
                // Ordered before the await-using's dispose: it must never run against an in-flight
                // MoveNextAsync (a pending advance is the NORMAL state at the progress yields above,
                // so a torn-down stream is routinely suspended exactly there).
                await ObserveAbandonedAdvanceAsync(moveNext, runCts).ConfigureAwait(false);
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
            // One unconditional frame to flip the client into the sweep phase. The throttled poll
            // below only fires while an advance is PENDING, and now that the sweep streams chunks
            // continuously each advance completes almost immediately — so without this the phase was
            // skipped entirely on a fast sweep and the UI jumped straight from scanning to building
            // (measured: the phase disappeared from the sequence after the sweep was made streaming).
            yield return new DryRunProgressResponse
            {
                Phase = DryRunProgressPhase.SweepingDestinations,
                SourceFiles = progressCounters.Sources,
                DestinationFiles = sweepIndexBase,
            };
            await using IAsyncEnumerator<Result<DryRunChunk, string>> sweepChunks = destinationProjector
                .SweepStreamAsync(
                    profile, survivors, truncated, sweepBudget, sweepIndexBase,
                    DryRunEngine.WireChunkByteBudget, progressCounters, runCts.Token)
                .GetAsyncEnumerator(runCts.Token);

            Task<bool> sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
            try
            {
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
            finally
            {
                // Same ordering as the file phase: never let the await-using dispose the sweep
                // enumerator while an advance is in flight, and unpark the sweep's producer so its
                // own dispose (which awaits the producer) can complete.
                await ObserveAbandonedAdvanceAsync(sweepMoveNext, runCts).ConfigureAwait(false);
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
            // Churn, not retention: everything this run allocated, nearly all of it dead by now.
            // This is the number the allocation-avoidance work (pooling, span probing) moves, and
            // in-run peak commit tracks it — the snapshot numbers above cannot show that.
            long allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore;
            logger.LogInformation(
                "Dry-run stream timings for profile {ProfileId}: total {TotalMs}ms " +
                "(engine stream {EngineMs}ms, destination sweep {SweepMs}ms, space estimator {EstimatorMs}ms), " +
                "{SourceCount} source files, {DestCount} destination files (+{SweepCount} swept); " +
                "memory managed {ManagedMb}MB, GC heap {HeapMb}MB, GC committed {CommittedMb}MB, " +
                "process private {PrivateMb}MB, allocated {AllocatedMb}MB",
                typed.ProfileId, totalWatch.ElapsedMilliseconds, engineMs, sweepWatch.ElapsedMilliseconds,
                estimatorWatch.ElapsedMilliseconds, emitted, destinationCount, sweptCount,
                managedBytes >> 20, gcInfo.HeapSizeBytes >> 20, gcInfo.TotalCommittedBytes >> 20,
                privateBytes >> 20, allocatedBytes >> 20);
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

    /// <summary>Makes a phase's teardown safe when this iterator is disposed with an advance still in
    /// flight — the NORMAL suspension state at the interleaved progress yields, and one the server
    /// reaches routinely (a failed frame write on client disconnect disposes the handler without
    /// cancelling its token, which is the server-lifetime token).
    ///
    /// <para>Two hazards, one ordering: disposing a compiler-generated async iterator while its
    /// MoveNextAsync is pending throws (abandoning the underlying pipeline/producer entirely), and the
    /// sweep enumerator's own dispose awaits a producer that only unparks on cancellation. So: cancel
    /// the run's linked token, then await the pending advance — only after that may the enclosing
    /// await-using dispose the enumerator. On every non-abandonment path the advance is already
    /// consumed and this is a completed-task no-op that cancels nothing.</para></summary>
    private async Task ObserveAbandonedAdvanceAsync(Task<bool> pendingAdvance, CancellationTokenSource runCts)
    {
        if (!pendingAdvance.IsCompleted)
            runCts.Cancel();
        try
        {
            await pendingAdvance.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last resort (this runs in a finally and must not throw): the expected shape is the
            // OperationCanceledException from the cancel above; a genuine fault was either already
            // surfaced as a failure item on the normal path or belongs to a stream nobody is
            // consuming anymore.
            logger.LogDebug(ex, "Dry-run stream advance abandoned during teardown");
        }
    }

    /// <summary>Converts the engine's stringy slices to the normalized wire shape. One instance owns
    /// the stream's whole directory-index space — the file-phase chunks and the handler-emitted sweep
    /// chunks extend the same table, so every index stays a valid position into the client's
    /// concatenated <c>Directories</c> list. Each outgoing chunk carries exactly the directory
    /// entries it references first (<see cref="DryRunDirectoryTableBuilder.FlushNew"/>), before the
    /// files/ops that use them.
    /// Internal (not private) for one reason: the per-entry allocation gauge test brackets
    /// <see cref="Convert(DryRunChunk)"/> with <see cref="GC.GetAllocatedBytesForCurrentThread"/> —
    /// the CI regression gate for the converter's allocation budget.</summary>
    internal sealed class WireChunkConverter(bool recycleWireRecords = true)
    {
        private readonly DryRunDirectoryTableBuilder _dirs = new();
        /// <summary>Wire-DTO pools plus the previously returned response. The production consumer
        /// (<c>IpcServer.ServeStreamAsync</c>) serializes each frame BEFORE requesting the next, so
        /// by the time <see cref="Convert(DryRunChunk)"/> runs again the previous response's records
        /// are provably off the wire and can be reused — one chunk's worth of DTOs serves the whole
        /// stream instead of one per entry. A consumer that buffers responses across chunks (some
        /// in-proc tests) must construct with <c>recycleWireRecords: false</c>; the byte-identical
        /// on/off round-trip test is what keeps the recycling path honest. The stream's final
        /// response is never recycled — one chunk of garbage, not one per chunk.</summary>
        private readonly Stack<DryRunFile> _filePool = new();
        private readonly Stack<DryRunOperation> _opPool = new();
        private readonly Stack<List<DryRunFile>> _fileListPool = new();
        private readonly Stack<List<DryRunOperation>> _opListPool = new();
        private DryRunChunkResponse? _previous;
        /// <summary>Reference-keyed (path → converted triple + root) memo, cleared per chunk. An op's
        /// <c>Path</c>/<c>Root</c> are usually the SAME string instances as its file's — every sweep
        /// pair shares them by construction, and source file/op pairs share the payload's path — so
        /// the ops loop can reuse the files loop's conversion instead of re-deriving (and
        /// re-allocating) the file name. A miss (e.g. a New/Rename op whose resulting path exists on
        /// no file) just pays the normal conversion. Reference equality is the point, not an
        /// optimization shortcut: it is what makes a hit PROVABLY the same string without comparing
        /// characters. Cleared per chunk so the memo never outlives the strings it keys on (pooled
        /// carriers swap their strings out between chunks).</summary>
        private readonly Dictionary<string, (int DirIndex, string FileName, int RootDirIndex, string Root)> _sharedPathMemo =
            new(ReferenceEqualityComparer.Instance);

        public DryRunChunkResponse Convert(DryRunChunk slice) =>
            Convert(slice.SourceFiles, slice.DestinationFiles, slice.SourceOperations, slice.DestinationOperations);

        public DryRunChunkResponse Convert(
            IReadOnlyList<IPhysicalFileView> sourceFiles, IReadOnlyList<IPhysicalFileView> destinationFiles,
            IReadOnlyList<IFileOperationView> sourceOps, IReadOnlyList<IFileOperationView> destinationOps)
        {
            // Reclaim the previous response's DTOs first — the caller has provably finished with it
            // (this converter is called once per chunk, after the previous frame went out).
            if (recycleWireRecords)
                RecyclePrevious();
            // Files convert BEFORE ops (memo fill order), matching the wire lists' order anyway.
            _sharedPathMemo.Clear();
            List<DryRunFile> wireSourceFiles = RentFileList(sourceFiles.Count);
            foreach (IPhysicalFileView f in sourceFiles)
                wireSourceFiles.Add(ConvertFile(f));
            List<DryRunFile> wireDestinationFiles = RentFileList(destinationFiles.Count);
            foreach (IPhysicalFileView f in destinationFiles)
                wireDestinationFiles.Add(ConvertFile(f));
            List<DryRunOperation> wireSourceOps = RentOpList(sourceOps.Count);
            foreach (IFileOperationView o in sourceOps)
                wireSourceOps.Add(ConvertOp(o));
            List<DryRunOperation> wireDestinationOps = RentOpList(destinationOps.Count);
            foreach (IFileOperationView o in destinationOps)
                wireDestinationOps.Add(ConvertOp(o));

            DryRunChunkResponse response = new()
            {
                Directories = _dirs.FlushNew(),
                SourceFiles = wireSourceFiles,
                DestinationFiles = wireDestinationFiles,
                SourceOperations = wireSourceOps,
                DestinationOperations = wireDestinationOps,
            };
            _previous = response;
            return response;
        }

        private void RecyclePrevious()
        {
            if (_previous is not { } previous)
                return;
            _previous = null;
            // The response's lists are always this converter's own (created in Convert above), so the
            // casts hold by construction; the directory slice stays with the GC (tiny, and its
            // entries are shared immutable records).
            ReturnFiles((List<DryRunFile>)previous.SourceFiles);
            ReturnFiles((List<DryRunFile>)previous.DestinationFiles);
            ReturnOps((List<DryRunOperation>)previous.SourceOperations);
            ReturnOps((List<DryRunOperation>)previous.DestinationOperations);
        }

        private void ReturnFiles(List<DryRunFile> list)
        {
            foreach (DryRunFile f in list)
            {
                f.FileName = "";   // don't pin the name from a retained slot
                _filePool.Push(f);
            }
            list.Clear();
            _fileListPool.Push(list);
        }

        private void ReturnOps(List<DryRunOperation> list)
        {
            foreach (DryRunOperation o in list)
            {
                o.FileName = "";
                o.Detail = null;
                _opPool.Push(o);
            }
            list.Clear();
            _opListPool.Push(list);
        }

        private List<DryRunFile> RentFileList(int capacity)
        {
            if (_fileListPool.Count == 0)
                return new List<DryRunFile>(capacity);
            List<DryRunFile> list = _fileListPool.Pop();
            list.EnsureCapacity(capacity);
            return list;
        }

        private List<DryRunOperation> RentOpList(int capacity)
        {
            if (_opListPool.Count == 0)
                return new List<DryRunOperation>(capacity);
            List<DryRunOperation> list = _opListPool.Pop();
            list.EnsureCapacity(capacity);
            return list;
        }

        private DryRunFile ConvertFile(IPhysicalFileView f)
        {
            int dirIndex;
            string fileName;
            int rootDirIndex;
            // Fast path for the sweep's carriers: they hold (directory, name) and never materialize
            // a joined path, so convert from the pair — the wire even reuses the enumeration's name
            // string. The memo is pointless there (the pair conversion is already allocation-free)
            // and touching Path at all would defeat the carrier's laziness.
            if (f is PooledPhysicalFile { DirectoryHint: { } fileDir } pooledFile)
            {
                (dirIndex, fileName, rootDirIndex) = _dirs.Convert(fileDir, pooledFile.FileName, f.Root);
            }
            else
            {
                (dirIndex, fileName, rootDirIndex) = _dirs.Convert(f.Path, f.Root);
                _sharedPathMemo[f.Path] = (dirIndex, fileName, rootDirIndex, f.Root);
            }
            DryRunFile wire = _filePool.Count > 0
                ? _filePool.Pop()
                : new DryRunFile { DirIndex = 0, FileName = "", RootDirIndex = 0, Length = 0, LastWritten = default };
            // Every property is (re)assigned — a rented instance keeps its previous values.
            wire.DirIndex = dirIndex;
            wire.FileName = fileName;
            wire.RootDirIndex = rootDirIndex;
            wire.Length = f.Length;
            wire.LastWritten = f.LastWritten;
            wire.IsReparsePoint = f.IsReparsePoint;
            return wire;
        }

        private DryRunOperation ConvertOp(IFileOperationView o)
        {
            (int DirIndex, string FileName, int RootDirIndex, string Root) hit;
            // Same fast path as ConvertFile — sweep ops carry the identical (directory, name) pair
            // as their file, so the directory-table probe hits the entry the file just ensured.
            if (o is PooledFileOperation { DirectoryHint: { } opDir } pooledOp)
            {
                (hit.DirIndex, hit.FileName, hit.RootDirIndex) = _dirs.Convert(opDir, pooledOp.FileName, o.Root);
            }
            // Both Path AND Root must be the file's instances — a same-path op under a different
            // root (nothing produces one today) would need its own root resolution, so it misses.
            else if (!_sharedPathMemo.TryGetValue(o.Path, out hit) || !ReferenceEquals(hit.Root, o.Root))
            {
                (hit.DirIndex, hit.FileName, hit.RootDirIndex) = _dirs.Convert(o.Path, o.Root);
            }
            DryRunOperation wire = _opPool.Count > 0
                ? _opPool.Pop()
                : new DryRunOperation { DirIndex = 0, FileName = "", RootDirIndex = 0, Kind = default };
            wire.DirIndex = hit.DirIndex;
            wire.FileName = hit.FileName;
            wire.RootDirIndex = hit.RootDirIndex;
            wire.Kind = o.Kind;
            wire.SourceIndex = o.SourceIndex;
            wire.SubjectIndex = o.SubjectIndex;
            wire.SourceDisposition = o.SourceDisposition;
            wire.Detail = o.Detail;
            return wire;
        }
    }
}
