using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.DryRun;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
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
/// frame (matching DryRunHandler's error codes).
/// <para>The handler no longer sequences the run itself: <see cref="IProfilePlanner"/> owns the
/// source-phase → survivor-set → destination-sweep arithmetic, shared with the live run pipeline so a
/// preview and a real run can never disagree about the work. What is left here is purely the wire —
/// interleaved progress frames, the normalized chunk conversion, and the end-of-run accounting.</para></summary>
public sealed class DryRunStreamHandler(
    ILogger<DryRunStreamHandler> logger, IProfilePlanner planner, IProfileCatalog catalog, TimeProvider time,
    IEngineEventBus eventBus, IMemoryTrimCoordinator trimCoordinator)
    : IIpcStreamingRequestHandler
{
    /// <summary>Spacing of the interleaved <see cref="DryRunProgressResponse"/> frames. The handler
    /// samples the shared counters on this timer while a discovery phase is in flight, so throttling
    /// is structural (at most ~10 frames/sec regardless of file rate) and the hot per-file paths pay
    /// only an uncontended increment.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

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
    /// contract <see cref="PlanChunk"/> puts on the carriers underneath). Serialize, copy, or otherwise
    /// finish with each frame before advancing; never buffer the records themselves across advances.
    /// <c>IpcServer.ServeStreamAsync</c> — the production consumer — writes each frame to the pipe
    /// before its next <c>MoveNextAsync</c>, which is what makes this safe; a consumer that cannot
    /// honor it constructs the handler with <see cref="RecycleWireRecords"/> off (buffering a recycled
    /// frame is silent, not an exception: every file name reads back empty and every Detail null).</summary>
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
        // plan advance is still in flight. ct is the SERVER's token — it fires on shutdown, never on
        // client disconnect — so when the server abandons this stream (a frame write failed mid-run)
        // nothing else would ever stop the plan's pipeline or the sweep's producer.
        using CancellationTokenSource runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Phase attribution for the whole streamed run: total wall time here, with the planner
        // reporting its own engine/sweep/estimator split.
        Stopwatch totalWatch = Stopwatch.StartNew();
        // One converter for the whole stream: it owns the report's directory-index space across the
        // source phase AND the sweep, so each outgoing chunk carries exactly its first-referenced
        // directory entries with globally valid indices.
        WireChunkConverter converter = new(RecycleWireRecords);
        // Live discovery counters, incremented by the engine's scan pump (sources) and the sweep's
        // walkers (destinations); sampled on ProgressInterval below so the interleaved progress
        // frames stay throttled no matter how fast files are found.
        DryRunProgressCounters progressCounters = new();
        // The planner's running totals, truncation flags, and (on completion) space projection.
        PlanState plan = new();

        await using (IAsyncEnumerator<Result<PlanChunk, string>> chunks = planner
            .PlanAsync(profile, typed.ScopePath, plan, progressCounters, runCts.Token)
            .GetAsyncEnumerator(runCts.Token))
        {
            // The engine's whole scan+evaluate pipeline runs inside the FIRST MoveNextAsync (no chunk
            // exists before the whole tree has been walked and spooled), and a big sweep can spend seconds
            // between chunks — so discovery progress is merged into the stream by polling the shared
            // counters while each advance is pending. Only a changed count is worth a frame: a
            // stalled phase goes quiet instead of repeating itself.
            Task<bool> moveNext = chunks.MoveNextAsync().AsTask();
            try
            {
                // Which phase the counters should be read as. The planner announces the destination
                // phase with a zero-entry PhaseStarted marker, so this stays correct even when the
                // sweep goes on to yield no data at all.
                PlanPhase phase = PlanPhase.Sources;
                long lastSources = -1;
                long lastDestinations = -1;
                while (true)
                {
                    while (await AsyncIteratorHeartbeat
                        .WaitForTickAsync(moveNext, ProgressInterval, ct).ConfigureAwait(false))
                    {
                        if (phase == PlanPhase.Sources && progressCounters.Sources != lastSources)
                        {
                            lastSources = progressCounters.Sources;
                            yield return new DryRunProgressResponse
                            {
                                Phase = DryRunProgressPhase.ScanningSources,
                                SourceFiles = lastSources,
                                DestinationFiles = plan.DestinationFiles,
                            };
                        }
                        else if (phase == PlanPhase.Destinations && progressCounters.Destinations != lastDestinations)
                        {
                            lastDestinations = progressCounters.Destinations;
                            yield return new DryRunProgressResponse
                            {
                                Phase = DryRunProgressPhase.SweepingDestinations,
                                SourceFiles = progressCounters.Sources,
                                DestinationFiles = plan.SweepIndexBase + lastDestinations,
                            };
                        }
                    }
                    // Propagates plan faults and cancellation exactly as a plain foreach would.
                    if (!await moveNext.ConfigureAwait(false))
                        break;
                    Result<PlanChunk, string> chunk = chunks.Current;

                    if (chunk.TryGetError(out string? error))
                    {
                        yield return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = error };
                        yield break;
                    }
                    chunk.TryGetValue(out PlanChunk slice);
                    phase = slice.Phase;

                    if (slice.PhaseStarted)
                    {
                        // One unconditional frame to flip the client into the new phase. The throttled
                        // poll above only fires while an advance is PENDING, and a streamed sweep
                        // completes each advance almost immediately — so without this the phase was
                        // skipped entirely on a fast sweep and the UI jumped straight from scanning to
                        // building (measured: the phase disappeared once the sweep became streaming).
                        yield return new DryRunProgressResponse
                        {
                            Phase = DryRunProgressPhase.SweepingDestinations,
                            SourceFiles = progressCounters.Sources,
                            DestinationFiles = plan.SweepIndexBase,
                        };
                        moveNext = chunks.MoveNextAsync().AsTask();
                        continue;
                    }

                    // Updated per chunk rather than once at the end so a run that is cancelled or
                    // errors out half way still reports the memory it actually churned.
                    trimScope.Units = plan.SourceFiles + plan.DestinationFiles;
                    yield return converter.Convert(slice.Chunk);

                    moveNext = chunks.MoveNextAsync().AsTask();
                }
            }
            finally
            {
                // Ordered before the await-using's dispose: it must never run against an in-flight
                // MoveNextAsync (a pending advance is the NORMAL state at the progress yields above,
                // so a torn-down stream is routinely suspended exactly there).
                await AsyncIteratorTeardown.ObserveAbandonedAdvanceAsync(moveNext, runCts, logger).ConfigureAwait(false);
            }
        }

        // Discovery is done — one unconditional frame flips the client to its final phase, covering
        // client reassembly and the UI's report projection.
        yield return new DryRunProgressResponse
        {
            Phase = DryRunProgressPhase.BuildingLists,
            SourceFiles = progressCounters.Sources,
            DestinationFiles = plan.DestinationFiles,
        };

        if (logger.IsEnabled(LogLevel.Information))
        {
            // Three-way memory split. NOTE THE SAMPLE POINT: this runs at the end of the run but
            // BEFORE the iterator tears down, so the space estimator, the survivor set and the wire
            // directory table are all still rooted. It is therefore a NEAR-PEAK reading, not the
            // post-run residual — do not quote it as "what the service settles at". The residual
            // needs a sample after this method's frame is gone.
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
                typed.ProfileId, totalWatch.ElapsedMilliseconds, plan.EngineMs, plan.SweepMs,
                plan.EstimatorMs, plan.SourceFiles, plan.DestinationFiles, plan.SweptFiles,
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
        // The destination-sweep counterpart of the notice above: a Warning-severity fault (the scan
        // depth ceiling, or an unreadable subdirectory) means real files below that point were never
        // classified, so Mirror never previews/deletes them as orphans — this is the only place a user
        // would otherwise have no way to know the sweep was incomplete.
        if (plan.SweepFaultDetail is not null)
            eventBus.Publish(new EngineWarningEvent
            {
                AtUtc = time.GetUtcNow(),
                Message = $"The destination scan could not fully verify one or more target folders "
                    + $"({plan.SweepFaultDetail}). Files below that point are not previewed as Mirror deletions "
                    + "or shown as untouched. Increase Max Scan Depth in Settings if this is a "
                    + "legitimately deep tree, or check the path for a network-share symlink loop (see the "
                    + "service log for details).",
            });
        yield return new DryRunCompleteResponse
        {
            GeneratedAt = time.GetUtcNow(),
            Truncated = plan.Truncated,
            Space = plan.Space,
        };
    }

    /// <summary>Converts the engine's stringy slices to the normalized wire shape. One instance owns
    /// the stream's whole directory-index space — the file-phase chunks and the sweep chunks extend
    /// the same table, so every index stays a valid position into the client's concatenated
    /// <c>Directories</c> list. Each outgoing chunk carries exactly the directory
    /// entries it references first (<see cref="DryRunDirectoryTableBuilder.FlushNew"/>), before the
    /// files/ops that use them.
    /// Internal (not private) for one reason: the per-entry allocation gauge test brackets
    /// <see cref="Convert(DryRunChunk)"/> with <see cref="GC.GetAllocatedBytesForCurrentThread"/> —
    /// the CI regression gate for the converter's allocation budget.</summary>
    internal sealed class WireChunkConverter(bool recycleWireRecords = true)
    {
        private readonly DryRunDirectoryTableBuilder _dirs = new();
        /// <summary>The previously returned response, reused wholesale. The production consumer
        /// (<c>IpcServer.ServeStreamAsync</c>) serializes each frame BEFORE requesting the next, so by
        /// the time <see cref="Convert(DryRunChunk)"/> runs again the previous response is provably off
        /// the wire and its column buffers can be cleared and refilled — one chunk's worth of buffers
        /// serves the whole stream. A consumer that buffers responses across chunks (some in-proc tests)
        /// must construct with <c>recycleWireRecords: false</c>; the byte-identical on/off round-trip
        /// test is what keeps the recycling path honest.
        ///
        /// <para>This used to be four <see cref="Stack{T}"/> pools of <c>DryRunFile</c>/
        /// <c>DryRunOperation</c> DTOs and their lists, with hand-written return paths that also had to
        /// null out <c>FileName</c> so a parked instance did not pin a string. Columns removed all of
        /// it: there is no per-record DTO to pool, so recycling is <see cref="DryRunFileColumns.Clear"/>
        /// on buffers that keep their capacity.</para></summary>
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

        /// <param name="sourceTargetKinds">Parallel to <paramref name="sourceOps"/>: the
        /// <c>OperationKindMask</c> of each source's destination operations, or null to write zeroes.
        /// <para>A parameter rather than a field on <c>IFileOperationView</c> because only the snapshot
        /// PAGE path has this to say — the live plan builds a source op before it has projected anything,
        /// and widening the engine's operation view for a display aggregate the engine never computes
        /// would put the field somewhere nothing could fill it.</para></param>
        public DryRunChunkResponse Convert(
            IReadOnlyList<IPhysicalFileView> sourceFiles, IReadOnlyList<IPhysicalFileView> destinationFiles,
            IReadOnlyList<IFileOperationView> sourceOps, IReadOnlyList<IFileOperationView> destinationOps,
            IReadOnlyList<int>? sourceTargetKinds = null)
        {
            // Reuse the previous response's column buffers first — the caller has provably finished
            // with it (this converter is called once per chunk, after the previous frame went out).
            DryRunChunkResponse response = recycleWireRecords && _previous is { } reusable
                ? Recycled(reusable)
                : new DryRunChunkResponse();
            // Files convert BEFORE ops (memo fill order), matching the wire columns' order anyway.
            _sharedPathMemo.Clear();
            foreach (IPhysicalFileView f in sourceFiles)
                ConvertFile(f, response.SourceFiles);
            foreach (IPhysicalFileView f in destinationFiles)
                ConvertFile(f, response.DestinationFiles);
            for (int i = 0; i < sourceOps.Count; i++)
            {
                ConvertOp(
                    sourceOps[i], response.SourceOperations,
                    sourceTargetKinds is not null && i < sourceTargetKinds.Count ? sourceTargetKinds[i] : 0);
            }
            foreach (IFileOperationView o in destinationOps)
                ConvertOp(o, response.DestinationOperations, targetKinds: 0);

            // The directory slice is a fresh list per chunk either way: FlushNew hands over ownership
            // of the entries first referenced here, and they are few (one per new directory, not one
            // per file).
            IReadOnlyList<DryRunDirectory> newDirectories = _dirs.FlushNew();
            response.DirectoryName.Clear();
            response.DirectoryParentIndex.Clear();
            foreach (DryRunDirectory dir in newDirectories)
            {
                response.DirectoryName.Add(dir.Name);
                response.DirectoryParentIndex.Add(dir.ParentIndex);
            }
            _previous = response;
            return response;
        }

        /// <summary>Empties the previous response's columns while keeping their capacity, so the whole
        /// stream runs on one set of buffers. Nothing to un-pin per record the way the old DTO pools had
        /// to — <c>Clear</c> drops every string reference the columns held.</summary>
        private static DryRunChunkResponse Recycled(DryRunChunkResponse previous)
        {
            previous.SourceFiles.Clear();
            previous.DestinationFiles.Clear();
            previous.SourceOperations.Clear();
            previous.DestinationOperations.Clear();
            return previous;
        }

        private void ConvertFile(IPhysicalFileView f, DryRunFileColumns columns)
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
            columns.Add(dirIndex, fileName, rootDirIndex, f.Length, f.LastWritten, f.IsReparsePoint);
        }

        private void ConvertOp(IFileOperationView o, DryRunOperationColumns columns, int targetKinds)
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
            columns.Add(
                hit.DirIndex, hit.FileName, hit.RootDirIndex, o.Kind,
                o.SourceIndex, o.SubjectIndex, o.SourceDisposition, o.Detail, targetKinds);
        }
    }
}
