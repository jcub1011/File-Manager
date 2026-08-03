using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using FileManager.Core.Scanning;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>Read-only (I-DRYRUN-RO) sweep of the profile's target roots that discovers pre-existing
/// files no source writes to and classifies each: left in place
/// (<see cref="OperationKind.Untouched"/>), an orphan a real <c>SyncMode.Mirror</c> run would delete
/// (<see cref="OperationKind.Deleted"/>), or a reparse point the sweep declines to judge
/// (<see cref="OperationKind.Unknown"/>). The per-source-file phase already emits every destination
/// write (New/Overwrite/Rename/Skip) as an operation, so this only fills the gaps that phase leaves.
///
/// The sweep runs on the shared <see cref="IScanScheduler"/> (which owns the global and per-drive
/// thread budgets); this class supplies only policy via the session callbacks.
///
/// <para><b>Two output shapes, two ordering contracts.</b> <see cref="Project"/>/<see cref="Sweep"/>
/// (batched — the CLI and most unit tests) collect every candidate and sort by path in a final serial
/// merge, because that path truncates by byte budget and sortedness is what makes the kept set
/// reproducible. <see cref="SweepStreamAsync"/> (the GUI) emits chunks as the walk produces them, in
/// discovery order, and leaves sorting to the client — the same split the source phase already has
/// (see <c>DryRunEngine</c>'s type comment), and the reason a 357k-entry sweep no longer has to hold
/// every entry at once.</para>
///
/// <para><b>What the streamed path still guarantees deterministically:</b> the set of reported
/// entries, each entry's classification, each op's <c>SubjectIndex</c> pointing at its own file's
/// global position, and the <c>Root</c> reported for a file under nested target roots (always the
/// outermost — nested roots are pruned before the walk, see <see cref="PruneNestedRoots"/>).
/// <b>Explicitly NOT deterministic:</b> row order, and which subset survives a capped sweep. The
/// client re-sorts by path-relative-to-root for display, so service order was never observable
/// (<c>DryRunViewModel</c>'s <c>ComputeLoad</c>).</para>
///
/// The entry points differ in how survivors are collected: <see cref="Project"/> takes the full
/// destination-operations list (batched path, unit tests), while <see cref="AccumulateSurvivors"/> +
/// <see cref="Sweep"/>/<see cref="SweepStreamAsync"/> let a streaming caller feed operation chunks
/// incrementally and retain only the (small) survivor path set.</summary>
public sealed class DestinationProjector(
    ILogger<DestinationProjector> logger, IVolumeInfoProvider volumes, IScanScheduler scheduler)
{
    /// <summary>Chunks buffered ahead of the consumer on the streamed path. Small on purpose: the
    /// whole point is that the sweep's memory is one chunk plus this buffer, independent of how many
    /// files the target roots hold. A full channel parks the walk, which is the back-pressure that
    /// bounds it.</summary>
    private const int StreamChunkBufferCapacity = 2;

    /// <summary>Test seam: handed each <see cref="SweepStreamAsync"/> call's carrier pool as the
    /// stream starts, so a test can hold the pool and assert the borrow==return and bounded-retention
    /// properties after consuming the stream. A <em>property</em> holding the last pool would root it
    /// on this object, which the service registers as a singleton
    /// (<c>EngineComposition</c>) — a run's carriers would then outlive the run, which is precisely
    /// the residual footprint the pooling exists to avoid. Null in production.</summary>
    internal Action<SweepCarrierPool>? SweepStreamPoolObserver { get; set; }

    /// <summary>Adds every resulting destination path a batch of destination operations accounts for
    /// to <paramref name="survivors"/> — so the sweep never re-reports a path a source already writes
    /// to (or the pre-existing file a rename was routed around, which the engine emits as an explicit
    /// Untouched op). Safe to call repeatedly across streamed chunks.</summary>
    public static void AccumulateSurvivors(SurvivorSet survivors, IReadOnlyList<IFileOperationView> destinationOperations)
    {
        ArgumentNullException.ThrowIfNull(survivors);
        ArgumentNullException.ThrowIfNull(destinationOperations);
        foreach (IFileOperationView op in destinationOperations)
            AddNormalized(survivors, op.Path);
    }

    /// <summary>Convenience for the batched path and unit tests: builds the survivor set from the full
    /// destination-operations list, then sweeps.</summary>
    public DestinationSweepResult Project(
        Profile profile, IReadOnlyList<VirtualFileOperation> destinationOperations, bool truncated, CancellationToken ct)
    {
        SurvivorSet survivors = new();
        AccumulateSurvivors(survivors, destinationOperations);
        return Sweep(profile, survivors, truncated, ct);
    }

    /// <summary>Batched sweep: collects every classified survivor, then sorts by path so the output is
    /// reproducible under the caller's byte-budget truncation. Each returned op's
    /// <see cref="VirtualFileOperation.SubjectIndex"/> indexes into the returned
    /// <see cref="DestinationSweepResult.Files"/> (op[i] → file[i]); a caller merging into a larger
    /// report offsets by the destination files already collected. Concurrency and per-drive budgets
    /// are the scheduler's; the result order is deterministic regardless.
    /// <para>For a large target tree prefer <see cref="SweepStreamAsync"/> — this one holds every
    /// entry in memory at once by construction, which is exactly what the sort costs.</para></summary>
    /// <param name="truncated">When the source pass was cut short, the survivor set is a prefix, so
    /// every "no source writes here" judgement is untrustworthy — a file we'd call an orphan (or
    /// Untouched) may well be written by an un-evaluated source. In that case we emit NOTHING rather
    /// than fabricate deletions/untouched entries.</param>
    /// <param name="maxEntries">Best-effort upper bound on emitted entries — workers stop feeding the
    /// sink once it is crossed and the merge trims to exactly this many, marking the result capped.</param>
    /// <param name="progress">When supplied, its destination counter is incremented per classified
    /// file so a caller can sample it for live progress.</param>
    public DestinationSweepResult Sweep(
        Profile profile, SurvivorSet survivors, bool truncated, CancellationToken ct,
        int maxEntries = int.MaxValue, DryRunProgressCounters? progress = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(survivors);

        if (truncated)
            return new DestinationSweepResult([], []);

        SweepBudget budget = new(maxEntries);
        if (PlanWalk(profile, survivors, budget) is not { } plan)
            return new DestinationSweepResult([], []);

        Stopwatch walkWatch = Stopwatch.StartNew();
        List<Candidate> collected = [];
        foreach (Candidate candidate in EnumerateCandidates(plan, ct))
        {
            collected.Add(candidate);
            progress?.DestinationDiscovered();
        }
        walkWatch.Stop();

        Stopwatch mergeWatch = Stopwatch.StartNew();
        DestinationSweepResult merged = Merge(collected, budget.Capped, maxEntries);
        mergeWatch.Stop();

        return merged with { WalkMs = walkWatch.ElapsedMilliseconds, MergeMs = mergeWatch.ElapsedMilliseconds };
    }

    /// <summary>Streamed sweep: emits <see cref="DryRunChunk"/>s as the walk produces them, so the
    /// sweep's live set is one chunk plus a two-chunk buffer rather than one
    /// <c>PhysicalFile</c> + <c>VirtualFileOperation</c> + path string for every pre-existing file
    /// under every target root. That difference is the whole reason this exists: at the workload this
    /// was written for (357,000 destination files) the batched shape holds ~145 MB before it emits
    /// anything, and the copy the caller then made of it added ~23 MB more.
    ///
    /// <para>Chunks carry destination entries only (no source half). Ops are already GLOBAL: each
    /// op's <see cref="VirtualFileOperation.SubjectIndex"/> is
    /// <paramref name="destinationIndexBase"/> plus its own file's position across the whole stream,
    /// so the caller appends chunks in receive order and nothing needs re-indexing.</para>
    ///
    /// <para>A capped sweep is reported by a trailing empty chunk with
    /// <see cref="DryRunChunk.SweepCapped"/> set — the cap is only known once the walk ends, and a
    /// marker frame is cheaper than buffering a chunk to back-fill the flag. <c>ScanTruncated</c>
    /// cannot carry this: the caller ORs that in before the sweep even starts.</para>
    ///
    /// <para><b>Ownership contract:</b> a yielded chunk's file/op entries are pooled carriers
    /// (<see cref="SweepCarrierPool"/>), valid only until the consumer requests the NEXT chunk —
    /// advancing the enumerator is the signal that recycles them. Consume a chunk fully (convert it,
    /// fold it, copy what must outlive it) before advancing; never buffer the views themselves across
    /// chunks. The handler's frame loop already has exactly this shape.</para></summary>
    /// <param name="chunkByteBudget">Wire-shape byte budget per chunk (see
    /// <c>DryRunEngine.WireChunkByteBudget</c>) — the same currency and constant the file phase uses,
    /// so both phases put frames of the same size on the wire.</param>
    public async IAsyncEnumerable<Result<DryRunChunk, string>> SweepStreamAsync(
        Profile profile, SurvivorSet survivors, bool truncated,
        int maxEntries, int destinationIndexBase, int chunkByteBudget,
        DryRunProgressCounters? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(survivors);

        if (truncated)
            yield break;

        SweepBudget budget = new(maxEntries);
        if (PlanWalk(profile, survivors, budget) is not { } plan)
            yield break;

        Channel<DryRunChunk> channel = Channel.CreateBounded<DryRunChunk>(
            new BoundedChannelOptions(StreamChunkBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        // The producer gets a LINKED token, not the caller's: an abandoned consumer (the caller
        // disposed this enumerator early, without cancelling ct — e.g. the client disconnected and the
        // server tore the stream down) leaves the producer parked on the full channel with nobody ever
        // reading again. The finally below cancels this to unpark it; awaiting it with only the
        // caller's token would hang the dispose chain until service shutdown.
        using CancellationTokenSource producerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // The walk runs on a pool thread because IScanSession.Consume() blocks the calling thread by
        // contract. It writes whole chunks, so the async side never touches a blocking enumerable and
        // the bounded channel is what stops a fast walk outrunning a slow pipe.
        // Per-run carrier pool: the producer rents, the reader loop below recycles once the consumer
        // has moved past a chunk. Dropped with the run, so the residual footprint is untouched.
        SweepCarrierPool pool = new();
        SweepStreamPoolObserver?.Invoke(pool);

        Task producer = Task.Run(
            () => ProduceChunksAsync(
                plan, channel.Writer, pool, budget, maxEntries, destinationIndexBase, chunkByteBudget, progress,
                producerStop.Token),
            CancellationToken.None);

        IAsyncEnumerator<DryRunChunk> chunks = channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                DryRunChunk? chunk = null;
                string? failure = null;
                try
                {
                    if (await chunks.MoveNextAsync().ConfigureAwait(false))
                        chunk = chunks.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;   // cancellation keeps the enumerator contract (never a partial success)
                }
                catch (Exception ex)
                {
                    // The producer faulted and completed the channel with its exception. Surface it as
                    // a failure item so the caller ends the stream with a readable error rather than an
                    // exception escaping mid-enumeration. (Yielding has to happen outside the catch.)
                    logger.LogError(ex, "Destination sweep (stream) failed: {Message}", ex.Message);
                    failure = $"destination sweep failed: {ex.Message}";
                }

                if (failure is not null)
                {
                    yield return failure;
                    yield break;
                }
                if (chunk is null)
                    break;      // channel completed normally
                yield return Result<DryRunChunk, string>.Success(chunk);
                // Resuming here means the consumer asked for the NEXT chunk, so it is done with this
                // one — the yielded chunk's ownership contract (see the method doc). Recycling is an
                // optimization, not bookkeeping the pool depends on: an abandoned enumerator simply
                // never recycles and the per-run pool is dropped whole.
                pool.Recycle(chunk);
            }
        }
        finally
        {
            await chunks.DisposeAsync().ConfigureAwait(false);
            // Unpark an abandoned producer BEFORE observing it: with the consumer gone the bounded
            // channel never drains, so a producer mid-WriteAsync would otherwise keep this await (and
            // the whole dispose chain above it) waiting until the caller's token finally fires — which
            // for the IPC server is service shutdown, not client disconnect. A no-op when the producer
            // already completed.
            producerStop.Cancel();
            // Always observe the producer: an abandoned enumerator (the consumer broke early) would
            // otherwise leave the walk running with nobody reading its fault.
            try { await producer.ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Last resort: the fault was already surfaced above on the normal path; here it can
                // only be an early-break or cancellation unwind, which must still not be silent.
                logger.LogDebug(ex, "Destination sweep (stream) producer ended with an exception");
            }
        }
    }

    /// <summary>The streamed path's producer: walks, classifies, packs entries into byte-budgeted
    /// chunks with global indices, and completes the channel (with the fault, if it threw).</summary>
    private async Task ProduceChunksAsync(
        WalkPlan plan, ChannelWriter<DryRunChunk> writer, SweepCarrierPool pool, SweepBudget budget,
        int maxEntries, int destinationIndexBase, int chunkByteBudget,
        DryRunProgressCounters? progress, CancellationToken ct)
    {
        try
        {
            List<IPhysicalFileView> files = pool.RentFileList();
            List<IFileOperationView> ops = pool.RentOpList();
            long bytes = 0;
            int emitted = 0;
            // The walk's SweepBudget is a best-effort early exit shared by its workers; this serial
            // emitter is the single writer, so its own count is the authoritative cap.
            bool capped = false;

            foreach (Candidate candidate in EnumerateCandidates(plan, ct))
            {
                progress?.DestinationDiscovered();
                if (emitted >= maxEntries)
                {
                    capped = true;
                    break;   // more survived than the budget allows
                }

                // Rented carriers, not fresh records — every field is (re)assigned because a rented
                // carrier keeps its previous non-string fields. The location is the (directory,
                // name) PAIR: nothing downstream on the streamed path reads a joined Path (the wire
                // converter and the estimator both work from the pair), so no per-entry path string
                // exists anywhere in the sweep.
                (PooledPhysicalFile file, PooledFileOperation op) = pool.RentPair();
                if (candidate.Directory is { } directory)
                {
                    file.SetLocation(directory, candidate.Location);
                    op.SetLocation(directory, candidate.Location);
                }
                else
                {
                    // Full-path shape (see Candidate): the path is authoritative, so hand it over
                    // verbatim and let the wire converter derive the name from it. Off the hot path.
                    file.Path = candidate.Location;
                    op.Path = candidate.Location;
                }
                file.Root = candidate.Root;
                file.Length = candidate.Length;
                file.LastWritten = candidate.Modified;
                file.IsReparsePoint = candidate.IsReparsePoint;
                op.Root = file.Root;
                op.Kind = candidate.Kind;
                op.SourceIndex = -1;
                op.SubjectIndex = destinationIndexBase + emitted;
                op.SourceDisposition = null;
                op.Detail = candidate.Detail;
                files.Add(file);
                ops.Add(op);
                emitted++;
                bytes += DryRunEngine.WireFileUpperBoundBytes(candidate.Location)
                    + DryRunEngine.WireOpUpperBoundBytes(candidate.Location, candidate.Detail);
                if (bytes < chunkByteBudget)
                    continue;

                await writer.WriteAsync(new DryRunChunk([], files, [], ops), ct).ConfigureAwait(false);
                files = pool.RentFileList();
                ops = pool.RentOpList();
                bytes = 0;
            }

            if (files.Count > 0)
                await writer.WriteAsync(new DryRunChunk([], files, [], ops), ct).ConfigureAwait(false);
            if (capped || budget.Capped)
                await writer.WriteAsync(new DryRunChunk([], [], [], [], SweepCapped: true), ct).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception ex)
        {
            // Completing WITH the exception is what makes the reader's MoveNextAsync throw it, which is
            // where it becomes a failure item. Without this the reader would block forever.
            writer.Complete(ex);
        }
    }

    /// <summary>Everything the walk needs, resolved once: the session policy, the roots to submit, and
    /// the shared budget. Null when there is nothing to sweep (no resolvable target root).</summary>
    private sealed record WalkPlan(ScanSessionOptions Options, List<NormalizedPath> TargetRoots, bool Mirror);

    private WalkPlan? PlanWalk(Profile profile, SurvivorSet survivors, SweepBudget budget)
    {
        // Normalize source roots once, for the target-under-source exclusion: a target root may
        // legally contain the source files (the validator only warns on overlap), and those source
        // files must never be previewed as destination deletions.
        List<NormalizedPath> sourceRoots = [];
        foreach (SourceConfig source in profile.Sources)
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                sourceRoots.Add(root);

        bool mirror = profile.SyncMode == SyncMode.Mirror;

        // Collect target roots (paired with themselves so every file records its root), then drop any
        // root nested under another — see PruneNestedRoots.
        List<NormalizedPath> targetRoots = [];
        foreach (TargetConfig target in profile.Targets)
            if (NormalizedPath.Create(target.Path).TryGetValue(out NormalizedPath targetRoot))
                targetRoots.Add(targetRoot);
        PruneNestedRoots(targetRoots);

        if (targetRoots.Count == 0)
            return null;

        ScanSessionOptions options = new()
        {
            // Deliberate asymmetries vs. the source scan: (1) NO MaxDepth pruning — a true mirror
            // deletes deep orphans regardless of the source's depth filter; (2) reparse-point dirs are
            // never descended (junctions can loop or escape the tree).
            OnSubdirectory = static (entry, tag) =>
            {
                if (InfrastructurePaths.IsInfrastructureDirectoryName(entry.FileName))
                    return new ChildDecision(false, null);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    return new ChildDecision(false, null);
                return new ChildDecision(true, tag);
            },
            OnFile = (entry, _) =>
            {
                if (InfrastructurePaths.IsTempFileName(entry.FileName))
                    return false;
                // The enumerated path descends from a GetFullPath-canonicalized target root, so it is
                // itself canonical — and it is probed as a SPAN composed into a stack buffer, so the
                // per-file exclusion check allocates nothing at all (no path string, no wrapper).
                if (IsExcludedPath(entry, survivors, sourceRoots))
                    return false;
                // Best-effort budget: once crossed, stop emitting (the caller applies the exact cap).
                return budget.TryReserve();
            },
            // The sweep never surfaces faults as results (a missing/unopenable target root is simply
            // "nothing (more) to report there"); the scheduler still treats a Fatal as terminal for
            // its directory.
            OnFault = (fault, _) =>
            {
                logger.LogDebug("Destination sweep skipped/stopped an entry: {Message}", fault.Message);
                return null;
            },
        };

        return new WalkPlan(options, targetRoots, mirror);
    }

    /// <summary>Drops any target root equal to or nested under another. The outer root's walk already
    /// covers the inner one's subtree, so the same file set is reported either way — but the duplicate
    /// enumeration I/O disappears, and so does the need for a per-path dedup set (which at 357k paths
    /// would be ~80 MB, defeating the point of streaming).
    ///
    /// <para>It also fixes a latent non-determinism. The old merge deduped by keeping whichever copy
    /// the sort left first, but <c>List&lt;T&gt;.Sort</c> is an unstable introsort and the comparison
    /// was on <c>Path</c> alone — so the <c>Root</c> reported for a file under two overlapping target
    /// roots was arbitrary. After pruning it is always the outermost root.</para></summary>
    private static void PruneNestedRoots(List<NormalizedPath> roots)
    {
        if (roots.Count < 2)
            return;
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            for (int j = 0; j < roots.Count; j++)
            {
                if (i == j)
                    continue;
                // Equal roots: keep the earlier one so the survivor is stable (drop the later index).
                bool nested = roots[i].IsUnder(roots[j]) || (roots[i].Equals(roots[j]) && j < i);
                if (!nested)
                    continue;
                roots.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>Opens the scan session, submits the roots, and yields each classified survivor. Shared
    /// verbatim by both entry points — all sweep POLICY lives here and in <see cref="PlanWalk"/>, so
    /// the batched and streamed paths can never drift on what counts as an orphan. Blocks the calling
    /// thread while awaiting results (the <see cref="IScanSession.Consume"/> contract).</summary>
    private IEnumerable<Candidate> EnumerateCandidates(WalkPlan plan, CancellationToken ct)
    {
        using IScanSession session = scheduler.OpenSession(plan.Options, ct);
        foreach (NormalizedPath targetRoot in plan.TargetRoots)
        {
            (string key, DriveClass driveClass) = ResolveVolume(targetRoot.Value);
            session.Submit(new ScanWorkItem(targetRoot.Value, key, driveClass, targetRoot));
        }
        // Roots are all in: without this the session would finalize the instant outstanding
        // touches zero — e.g. an empty first target root finishing while ResolveVolume blocks
        // on the second — silently dropping every remaining root from the sweep.
        session.CompleteSubmissions();

        foreach (ScanResult result in session.Consume())
        {
            if (result.Entry is not FileSystemEntry fsEntry)
                continue;
            NormalizedPath rootTag = (NormalizedPath)result.Tag!;
            bool isReparse = (fsEntry.Attributes & FileAttributes.ReparsePoint) != 0;
            OperationKind kind = isReparse
                ? OperationKind.Unknown              // can't judge a reparse point
                : plan.Mirror
                    ? OperationKind.Deleted          // orphan a mirror would remove
                    : OperationKind.Untouched;       // pre-existing, left in place
            // Two location shapes, mirroring FileSystemEntry itself. Real enumeration carries a
            // directory hint and the (directory, name) PAIR is the location — no per-entry path
            // string anywhere in the sweep. A full-path-constructed entry (synthetic test schedulers,
            // FileSystemService's "Home") has a FileName that is a display name, NOT necessarily the
            // last segment (see FileSystemEntry.FileName), so its FullPath is authoritative and is
            // never re-derived: splitting and rejoining it would report the entry at a path other
            // than the one IsExcludedPath probed, which in Mirror mode is a Deleted op for a path
            // that does not exist.
            (string? directory, string location) = fsEntry.DirectoryHint is { } hint
                ? (hint, fsEntry.FileName)
                : ((string?)null, fsEntry.FullPath);
            yield return new Candidate(
                directory,
                location,
                rootTag.Value,
                fsEntry.Size,
                fsEntry.Modified,
                isReparse,
                kind,
                isReparse ? "reparse point (symlink/junction)" : null);
        }
    }

    /// <summary>Serial merge of the collected candidates into the index-paired result, for the BATCHED
    /// path only: sorts by path so the caller's byte-budget truncation keeps a reproducible set, and
    /// assigns each op's <see cref="VirtualFileOperation.SubjectIndex"/> so <c>Ops[i]</c> references
    /// <c>Files[i]</c>. Applies the authoritative <paramref name="maxEntries"/> cap.
    /// <para>No per-path dedup: nested target roots are pruned before the walk
    /// (<see cref="PruneNestedRoots"/>), so no path can be enumerated twice.</para></summary>
    private static DestinationSweepResult Merge(List<Candidate> collected, bool cappedDuringWalk, int maxEntries)
    {
        // The batched path's output is full-path records, so compose each candidate's path ONCE here
        // (bounded — this path collects under its walk budget), then sort on the composed strings.
        // The comparison is unchanged: full absolute path, OrdinalIgnoreCase.
        var paired = new (string Path, Candidate Candidate)[collected.Count];
        for (int i = 0; i < collected.Count; i++)
            paired[i] = (
                collected[i].Directory is { } directory
                    ? Path.Join(directory, collected[i].Location)
                    : collected[i].Location,          // full-path shape: authoritative, see Candidate
                collected[i]);
        Array.Sort(paired, static (a, b) =>
            string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        List<PhysicalFile> files = new(paired.Length);
        List<VirtualFileOperation> ops = new(paired.Length);
        bool capped = cappedDuringWalk;
        foreach ((string path, Candidate candidate) in paired)
        {
            if (files.Count >= maxEntries)
            {
                capped = true;   // more survived than the budget allows — trim here
                break;
            }

            int subjectIndex = files.Count;
            files.Add(new PhysicalFile
            {
                Path = path,
                Root = candidate.Root,
                Length = candidate.Length,
                LastWritten = candidate.Modified,
                IsReparsePoint = candidate.IsReparsePoint,
            });
            ops.Add(new VirtualFileOperation
            {
                Path = path,
                Root = candidate.Root,
                Kind = candidate.Kind,
                SourceIndex = -1,
                SubjectIndex = subjectIndex,
                Detail = candidate.Detail,
            });
        }
        return new DestinationSweepResult(files, ops, capped);
    }

    /// <summary>The volume key + drive class for a target root, used to size its per-drive scan
    /// budget. A key that cannot be resolved falls to a single synthetic local volume.</summary>
    private (string Key, DriveClass DriveClass) ResolveVolume(string path)
    {
        string key = volumes.GetVolumeKey(path).TryGetValue(out string? resolved) ? resolved : "local";
        return (key, volumes.GetDriveClass(path));
    }

    /// <summary>The sweep's per-file exclusion check ("a source writes here" / "this IS a source
    /// file"), against the entry's canonical full path composed into a stack buffer — the hot path
    /// pays no allocation. Falls back to the materialized path for full-path-constructed entries
    /// (synthetic scan schedulers in tests; real enumeration always carries a directory hint).
    /// Correctness guard: <c>Span_probe_agrees_with_NormalizedPath_probing</c> pins this against the
    /// NormalizedPath-based answer over adversarial paths.</summary>
    private static bool IsExcludedPath(FileSystemEntry entry, SurvivorSet survivors, List<NormalizedPath> sourceRoots)
    {
        if (entry.DirectoryHint is not { } directory)
        {
            ReadOnlySpan<char> full = Path.TrimEndingDirectorySeparator(entry.FullPath.AsSpan());
            return survivors.Contains(full) || IsUnderAnySource(full, sourceRoots);
        }

        // Join with Path.Join's separator rule: add one only when the directory doesn't end in one
        // (volume roots like "C:\" do). File names never carry a trailing separator, so the result
        // needs no trimming.
        bool needsSeparator = directory.Length > 0 && !Path.EndsInDirectorySeparator(directory);
        int length = directory.Length + (needsSeparator ? 1 : 0) + entry.FileName.Length;
        char[]? rented = null;
        Span<char> buffer = length <= 512 ? stackalloc char[512] : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(length));
        try
        {
            directory.CopyTo(buffer);
            int written = directory.Length;
            if (needsSeparator)
                buffer[written++] = Path.DirectorySeparatorChar;
            entry.FileName.CopyTo(buffer[written..]);
            written += entry.FileName.Length;

            ReadOnlySpan<char> composed = buffer[..written];
            return survivors.Contains(composed) || IsUnderAnySource(composed, sourceRoots);
        }
        finally
        {
            // try/finally, not a straight-line return: this runs per enumerated file on every scan
            // worker, so a throw from the probes above would leak one buffer PER FILE out of the
            // shared pool and degrade every other char[] renter in the process.
            if (rented is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static void AddNormalized(SurvivorSet set, string path)
    {
        // Create, never FromCanonical: destination op paths are built from the RAW profile string
        // (see docs/dry-run-service-memory.md §11) — GetFullPath here is what makes a target written
        // as "d:/out" or "D:\out\.\" match the sweep's canonical probes.
        if (NormalizedPath.Create(path).TryGetValue(out NormalizedPath normalized))
            set.Add(normalized);
    }

    private static bool IsUnderAnySource(ReadOnlySpan<char> canonicalPath, List<NormalizedPath> sourceRoots)
    {
        foreach (NormalizedPath root in sourceRoots)
        {
            if (NormalizedPath.IsEqualToOrUnder(canonicalPath, root))
                return true;
        }
        return false;
    }

    /// <summary>A classified survivor awaiting emission. Holds everything both output records need;
    /// <see cref="VirtualFileOperation.SubjectIndex"/> is assigned only at emission time (it depends on
    /// position, which differs between the sorted batched merge and the streamed discovery order), so
    /// it is absent here.</summary>
    /// <summary>One classified survivor, as raw fields rather than a materialized record — the two
    /// entry points want different output currencies (the batched merge builds immutable
    /// <see cref="PhysicalFile"/> records, the streamed producer fills pooled carriers), so the shared
    /// walk hands over facts and lets each output pipeline pay only its own allocation.</summary>
    /// <param name="Directory">The containing directory when the location is the enumeration's
    /// (directory, name) pair — shared by every sibling, which is what keeps the sweep free of
    /// per-entry path strings. Null when the entry was constructed from a full path, in which case
    /// <paramref name="Location"/> IS that path and is used verbatim.</param>
    /// <param name="Location">The file name under <paramref name="Directory"/>, or — when that is
    /// null — the entry's authoritative absolute path.</param>
    private readonly record struct Candidate(
        string? Directory, string Location, string Root, long Length, DateTimeOffset Modified,
        bool IsReparsePoint, OperationKind Kind, string? Detail);

    /// <summary>The best-effort emission budget shared by the walk's workers: reserves a slot per
    /// candidate, latching Capped once the reservations exceed the cap. An unbounded budget always
    /// succeeds.</summary>
    private sealed class SweepBudget(int maxEntries)
    {
        private int _produced;
        private int _capped;

        public bool Capped => Volatile.Read(ref _capped) != 0;

        public bool TryReserve()
        {
            if (maxEntries == int.MaxValue)
                return true;
            if (Interlocked.Increment(ref _produced) > maxEntries)
            {
                Volatile.Write(ref _capped, 1);
                return false;
            }
            return true;
        }
    }
}
