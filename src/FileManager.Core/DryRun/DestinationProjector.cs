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

    /// <summary>Adds every resulting destination path a batch of destination operations accounts for
    /// to <paramref name="survivors"/> — so the sweep never re-reports a path a source already writes
    /// to (or the pre-existing file a rename was routed around, which the engine emits as an explicit
    /// Untouched op). Safe to call repeatedly across streamed chunks.</summary>
    public static void AccumulateSurvivors(ISet<NormalizedPath> survivors, IReadOnlyList<IFileOperationView> destinationOperations)
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
        HashSet<NormalizedPath> survivors = [];
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
        Profile profile, ISet<NormalizedPath> survivors, bool truncated, CancellationToken ct,
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
    /// cannot carry this: the caller ORs that in before the sweep even starts.</para></summary>
    /// <param name="chunkByteBudget">Wire-shape byte budget per chunk (see
    /// <c>DryRunEngine.WireChunkByteBudget</c>) — the same currency and constant the file phase uses,
    /// so both phases put frames of the same size on the wire.</param>
    public async IAsyncEnumerable<Result<DryRunChunk, string>> SweepStreamAsync(
        Profile profile, ISet<NormalizedPath> survivors, bool truncated,
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
        Task producer = Task.Run(
            () => ProduceChunksAsync(
                plan, channel.Writer, budget, maxEntries, destinationIndexBase, chunkByteBudget, progress,
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
        WalkPlan plan, ChannelWriter<DryRunChunk> writer, SweepBudget budget,
        int maxEntries, int destinationIndexBase, int chunkByteBudget,
        DryRunProgressCounters? progress, CancellationToken ct)
    {
        try
        {
            List<PhysicalFile> files = [];
            List<VirtualFileOperation> ops = [];
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

                VirtualFileOperation op = new()
                {
                    Path = candidate.File.Path,
                    Root = candidate.File.Root,
                    Kind = candidate.Kind,
                    SourceIndex = -1,
                    SubjectIndex = destinationIndexBase + emitted,
                    Detail = candidate.Detail,
                };
                files.Add(candidate.File);
                ops.Add(op);
                emitted++;
                bytes += DryRunEngine.WireUpperBoundBytes(candidate.File) + DryRunEngine.WireUpperBoundBytes(op);
                if (bytes < chunkByteBudget)
                    continue;

                await writer.WriteAsync(new DryRunChunk([], files, [], ops), ct).ConfigureAwait(false);
                files = [];
                ops = [];
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

    private WalkPlan? PlanWalk(Profile profile, ISet<NormalizedPath> survivors, SweepBudget budget)
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
                // itself canonical — wrap it WITHOUT paying Create's per-file re-canonicalization.
                NormalizedPath filePath = NormalizedPath.FromCanonical(entry.FullPath);
                if (survivors.Contains(filePath))
                    return false;   // a source writes here — already an operation from the file phase
                if (IsUnderAnySource(filePath, sourceRoots))
                    return false;   // a source file that happens to live under a target root
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
            yield return new Candidate(
                NormalizedPath.FromCanonical(fsEntry.FullPath),
                new PhysicalFile
                {
                    Path = fsEntry.FullPath,
                    Root = rootTag.Value,
                    Length = fsEntry.Size,
                    LastWritten = fsEntry.Modified,
                    IsReparsePoint = isReparse,
                },
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
        collected.Sort(static (a, b) =>
            string.Compare(a.Path.Value, b.Path.Value, StringComparison.OrdinalIgnoreCase));

        List<PhysicalFile> files = new(collected.Count);
        List<VirtualFileOperation> ops = new(collected.Count);
        bool capped = cappedDuringWalk;
        foreach (Candidate candidate in collected)
        {
            if (files.Count >= maxEntries)
            {
                capped = true;   // more survived than the budget allows — trim here
                break;
            }

            int subjectIndex = files.Count;
            files.Add(candidate.File);
            ops.Add(new VirtualFileOperation
            {
                Path = candidate.File.Path,
                Root = candidate.File.Root,
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

    private static void AddNormalized(ISet<NormalizedPath> set, string path)
    {
        if (NormalizedPath.Create(path).TryGetValue(out NormalizedPath normalized))
            set.Add(normalized);
    }

    private static bool IsUnderAnySource(NormalizedPath path, List<NormalizedPath> sourceRoots)
    {
        foreach (NormalizedPath root in sourceRoots)
        {
            if (path.Equals(root) || path.IsUnder(root))
                return true;
        }
        return false;
    }

    /// <summary>A classified survivor awaiting emission. Holds everything both output records need;
    /// <see cref="VirtualFileOperation.SubjectIndex"/> is assigned only at emission time (it depends on
    /// position, which differs between the sorted batched merge and the streamed discovery order), so
    /// it is absent here.</summary>
    private readonly record struct Candidate(NormalizedPath Path, PhysicalFile File, OperationKind Kind, string? Detail);

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
