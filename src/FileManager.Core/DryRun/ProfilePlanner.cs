using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>Which discovery phase produced a <see cref="PlanChunk"/>. Consumers need this to label
/// progress and to know when the survivor set is complete; it is NOT a property of the chunk's
/// contents (a destination-phase chunk carries destination entries, but so can a source-phase one).</summary>
public enum PlanPhase
{
    Sources,
    Destinations,
}

/// <summary>One slice of a plan, tagged with its phase.
/// <para><b>Ownership contract (I-POOL-RECYCLE):</b> <see cref="Chunk"/>'s file/op entries may be
/// pool-owned carriers, valid only until the consumer requests the NEXT chunk. Consume it fully —
/// convert it, fold it, copy out anything that must outlive it — before advancing. Never retain the
/// views themselves, or any object reachable from them, across an advance.</para></summary>
/// <param name="PhaseStarted">Set on a zero-entry marker that announces a phase has begun, so a
/// consumer can flip its displayed phase even when that phase goes on to yield no data at all (an
/// empty target tree). A marker carries nothing to fold and must not become a data frame.</param>
/// <param name="SourceBase">Global position of this chunk's first source file, i.e. the count of
/// source files across every earlier chunk.</param>
/// <param name="DestinationBase">Global position of this chunk's first destination file. Operations
/// carry GLOBAL indices, so a consumer resolving an op to the file it acts on — which the run
/// snapshot must do to pair a <see cref="OperationKind.Deleted"/> op with the orphan it names —
/// subtracts this from the op's <c>SubjectIndex</c> to get the local position. Handing the base over
/// rather than making the consumer derive it from a running total is deliberate: the running total is
/// already advanced past this chunk by the time it is yielded.</param>
public readonly record struct PlanChunk(
    DryRunChunk Chunk, PlanPhase Phase, int SourceBase, int DestinationBase, bool PhaseStarted = false);

/// <summary>The running and final state of one <see cref="IProfilePlanner.PlanAsync"/> enumeration.
/// The planner owns every field; the consumer reads them. Supplied by the caller (rather than
/// returned) because an <c>IAsyncEnumerable</c> cannot hand back an out-of-band result, and the
/// consumer needs these values <em>while</em> it streams, not only at the end.</summary>
public sealed class PlanState
{
    /// <summary>Bound on the files one plan covers. Both phases share it: the source phase stops
    /// emitting past it, and the sweep's budget is what remains. Defaults to the engine's streamed
    /// cap; a test shrinks it so truncation is reachable without half a million files.</summary>
    public int MaxFiles { get; init; } = DryRunEngine.MaxStreamedFiles;

    /// <summary>Source files covered so far — also the base for the next chunk's source indices.</summary>
    public int SourceFiles { get; internal set; }

    /// <summary>Destination files covered so far, across BOTH phases — also the base for the next
    /// chunk's destination indices.</summary>
    public int DestinationFiles { get; internal set; }

    /// <summary>Destination files the sweep contributed (a subset of <see cref="DestinationFiles"/>),
    /// for the timing/volume log line.</summary>
    public int SweptFiles { get; internal set; }

    /// <summary><see cref="DestinationFiles"/> at the instant the sweep began: the fixed base the
    /// projector's own running position is added to. Zero until the sweep starts.</summary>
    public int SweepIndexBase { get; internal set; }

    /// <summary>Set when the plan does not cover everything it was asked to: the source scan hit its
    /// candidate bound, the sweep hit its entry bound, or the sweep could not fully walk a target
    /// root. A truncated plan's orphan judgements are unsound — a file no source appears to write to
    /// may well be written by an un-evaluated source — so a Mirror deletion must never act on one.</summary>
    public bool Truncated { get; internal set; }

    /// <summary>The first Warning-severity sweep fault's message (it already names the path), for the
    /// user-facing notice. Non-null implies <see cref="Truncated"/>.</summary>
    public string? SweepFaultDetail { get; internal set; }

    /// <summary>The space projection folded from the streamed chunks, or null when the plan was
    /// truncated (totals over a partial graph would be unsound). Set once the enumeration completes
    /// normally.</summary>
    public SpaceProjection? Space { get; internal set; }

    /// <summary>Wall time inside the engine's source stream, the destination sweep, and the space
    /// estimator's cumulative share. The phases do not overlap, but the estimator's time is folded
    /// inside the other two.</summary>
    public long EngineMs { get; internal set; }
    public long SweepMs { get; internal set; }
    public long EstimatorMs { get; internal set; }
}

/// <summary>Produces the work a profile implies: the per-source-file phase followed by the
/// destination sweep, as one tagged chunk stream.
///
/// <para><b>Why this exists.</b> A dry run and a live run must describe the SAME work — that is the
/// entire value of the preview, and under <see cref="SyncMode.Mirror"/> a live run that deletes
/// something the preview did not show is data loss. So the sequencing that turns a profile into work
/// lives here, once: run the source phase, accumulate the survivor set from its destination
/// operations, OR the scan's truncation in <em>before</em> the sweep (a prefix-only survivor set makes
/// every orphan call untrustworthy), gate the sweep on
/// <see cref="Profile.EffectiveScanDestination"/>, and bound it by what is left of the file budget.
/// <c>DryRunStreamHandler</c> renders the result to the wire; the run pipeline writes it to a
/// snapshot and then executes it. Neither owns the arithmetic.</para></summary>
public sealed class ProfilePlanner(
    ILogger<ProfilePlanner> logger,
    IDryRunEngine engine,
    DestinationProjector destinationProjector,
    IVolumeInfoProvider volumes,
    EngineConfig config) : IProfilePlanner
{
    /// <summary>Test seam mirroring <c>DryRunEngine.ChunkByteBudget</c>: production uses the wire
    /// budget so both phases put same-sized frames on the pipe.</summary>
    internal int ChunkByteBudget { get; init; } = DryRunEngine.WireChunkByteBudget;

    public async IAsyncEnumerable<Result<PlanChunk, string>> PlanAsync(
        Profile profile, string? scopePath, PlanState state,
        DryRunProgressCounters? progress = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);

        // The plan's own cancellation authority over its inner enumerators, cancelled only when THIS
        // iterator is torn down with an inner advance still in flight. The caller's token may be a
        // server-lifetime token that never fires on client disconnect, so without this an abandoned
        // plan would leave the engine pipeline and the sweep's producer running with nobody reading.
        using CancellationTokenSource planCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Byte/space projection, folded from the same chunks as they stream by — no extra buffering.
        // MirrorDeletion decides only whether the orphans' bytes are still occupied while the copies
        // land, which is why it belongs to the projection and not to the work list.
        DryRunSpaceEstimator estimator = new(
            volumes, config.PreflightSafetyMarginBytes, profile.Policies.MirrorDeletion);
        bool stageOverwrites = profile.Policies.OverwriteHandling == OverwriteHandling.StageOverwrites;
        Stopwatch estimatorWatch = new();
        Stopwatch phaseWatch = Stopwatch.StartNew();

        // Only the (small) set of destination paths a source writes to, accumulated as chunks stream
        // by — never the file objects. A SurvivorSet rather than a HashSet<NormalizedPath> so the
        // sweep can probe it by span, with no per-file path string.
        SurvivorSet survivors = new();

        // Folds one slice into the estimator and (source phase only) the survivor set, then advances
        // the index bases. Ordering is load-bearing and matches what the wire handler did inline: the
        // fold must see the bases as they were BEFORE this chunk, because a chunk's op indices are
        // global and the estimator resolves them against the chunk's own lists.
        (int SourceBase, int DestinationBase) Fold(DryRunChunk slice, bool collectSurvivors)
        {
            (int sourceBase, int destinationBase) = (state.SourceFiles, state.DestinationFiles);
            estimatorWatch.Start();
            estimator.Accumulate(
                slice.SourceFiles, slice.DestinationFiles, slice.SourceOperations, slice.DestinationOperations,
                state.SourceFiles, state.DestinationFiles, stageOverwrites);
            estimatorWatch.Stop();
            // Survivors come from the source phase only — they are what the sweep tests each
            // pre-existing file against. Feeding the sweep's own output back could never change an
            // answer (the sweep is finished with the set by then) and would grow the very structure
            // streaming exists to keep small.
            if (collectSurvivors)
                DestinationProjector.AccumulateSurvivors(survivors, slice.DestinationOperations);
            state.SourceFiles += slice.SourceFiles.Count;
            state.DestinationFiles += slice.DestinationFiles.Count;
            return (sourceBase, destinationBase);
        }

        // ---- phase 1: the per-source-file stream -------------------------------------------------
        await using (IAsyncEnumerator<Result<DryRunChunk, string>> chunks = engine
            .SimulateStreamAsync(profile, scopePath, progress, planCts.Token)
            .GetAsyncEnumerator(planCts.Token))
        {
            Task<bool> moveNext = chunks.MoveNextAsync().AsTask();
            try
            {
                while (true)
                {
                    if (!await moveNext.ConfigureAwait(false))
                        break;
                    Result<DryRunChunk, string> chunk = chunks.Current;
                    if (chunk.TryGetError(out string? error))
                    {
                        yield return error;
                        yield break;
                    }
                    chunk.TryGetValue(out DryRunChunk? slice);

                    // The engine sets ScanTruncated once its candidate scan is cut short. OR it in
                    // BEFORE the sweep so a prefix-only survivor set can never drive a bogus
                    // Mirror-deletion plan.
                    state.Truncated |= slice!.ScanTruncated;
                    (int sourceBase, int destinationBase) = Fold(slice, collectSurvivors: true);
                    yield return new PlanChunk(slice, PlanPhase.Sources, sourceBase, destinationBase);

                    if (state.SourceFiles >= state.MaxFiles)
                    {
                        logger.LogWarning(
                            "Plan for profile {ProfileId} hit the {Cap:N0}-file safety bound; work list truncated",
                            profile.Id, state.MaxFiles);
                        state.Truncated = true;
                        break;
                    }
                    moveNext = chunks.MoveNextAsync().AsTask();
                }
            }
            finally
            {
                // Ordered before the await-using's dispose: it must never run against an in-flight
                // MoveNextAsync. A pending advance is the normal state whenever the consumer
                // abandoned us mid-chunk.
                await AsyncIteratorTeardown.ObserveAbandonedAdvanceAsync(moveNext, planCts, logger).ConfigureAwait(false);
            }
        }
        state.EngineMs = phaseWatch.ElapsedMilliseconds;

        // ---- phase 2: the destination sweep -----------------------------------------------------
        // AdditiveArchive may opt out (ScanDestination = false); Mirror always sweeps, because the
        // sweep is the ONLY source of its orphan set. Suppressed entirely when truncated — the
        // stream yields nothing in that case anyway, but short-circuiting here also skips the
        // non-trivial root resolution and scan-session setup.
        if (!profile.EffectiveScanDestination || state.Truncated)
        {
            Finalize(state, estimator, estimatorWatch, config);
            yield break;
        }

        state.SweepIndexBase = state.DestinationFiles;
        Stopwatch sweepWatch = Stopwatch.StartNew();

        // One zero-entry marker so the consumer can flip its phase even if the sweep goes on to
        // yield nothing: each sweep advance now completes almost immediately, so a consumer that
        // only relabels on a pending advance would skip the phase entirely on a fast sweep.
        yield return new PlanChunk(
            EmptyChunk, PlanPhase.Destinations, state.SourceFiles, state.SweepIndexBase, PhaseStarted: true);

        await using (IAsyncEnumerator<Result<DryRunChunk, string>> sweepChunks = destinationProjector
            .SweepStreamAsync(
                profile, survivors, state.Truncated, Math.Max(0, state.MaxFiles - state.DestinationFiles),
                state.SweepIndexBase, ChunkByteBudget, progress, planCts.Token)
            .GetAsyncEnumerator(planCts.Token))
        {
            Task<bool> sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
            try
            {
                while (true)
                {
                    if (!await sweepMoveNext.ConfigureAwait(false))
                        break;
                    Result<DryRunChunk, string> sweepChunk = sweepChunks.Current;
                    if (sweepChunk.TryGetError(out string? sweepError))
                    {
                        yield return sweepError;
                        yield break;
                    }
                    sweepChunk.TryGetValue(out DryRunChunk? sweepSlice);

                    if (sweepSlice!.SweepCapped)
                    {
                        logger.LogWarning(
                            "Plan for profile {ProfileId}: the destination sweep hit the {Cap:N0}-entry bound; work list truncated",
                            profile.Id, state.MaxFiles);
                        state.Truncated = true;
                    }
                    if (sweepSlice.SweepFaulted)
                    {
                        logger.LogWarning(
                            "Plan for profile {ProfileId}: the destination sweep could not fully walk one or more " +
                            "target root(s): {Detail}; work list truncated",
                            profile.Id, sweepSlice.SweepFaultDetail);
                        state.Truncated = true;
                        state.SweepFaultDetail ??= sweepSlice.SweepFaultDetail;
                    }

                    state.SweptFiles += sweepSlice.DestinationFiles.Count;
                    // The capped/faulted signal rides an empty chunk; there is nothing to fold and
                    // nothing a consumer should render for it.
                    if (sweepSlice.DestinationFiles.Count == 0)
                    {
                        sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
                        continue;
                    }
                    (int sweepSourceBase, int sweepDestinationBase) = Fold(sweepSlice, collectSurvivors: false);
                    yield return new PlanChunk(
                        sweepSlice, PlanPhase.Destinations, sweepSourceBase, sweepDestinationBase);

                    sweepMoveNext = sweepChunks.MoveNextAsync().AsTask();
                }
            }
            finally
            {
                // Same ordering as phase 1, and additionally: the sweep enumerator's own dispose
                // awaits a producer that only unparks on cancellation, so the cancel must precede it.
                await AsyncIteratorTeardown.ObserveAbandonedAdvanceAsync(sweepMoveNext, planCts, logger).ConfigureAwait(false);
            }
        }
        sweepWatch.Stop();
        state.SweepMs = sweepWatch.ElapsedMilliseconds;
        Finalize(state, estimator, estimatorWatch, config);
    }

    /// <summary>A zero-entry chunk for the phase marker. Immutable and shared: it carries no carriers,
    /// so nothing can recycle out from under a consumer.</summary>
    private static readonly DryRunChunk EmptyChunk = new([], [], [], []);

    /// <summary>Completes the projection and the timing tally. Skipped on a truncated plan: totals
    /// over a partial graph would be unsound, and "will it fit" must never be answered optimistically.</summary>
    private static void Finalize(
        PlanState state, DryRunSpaceEstimator estimator, Stopwatch estimatorWatch, EngineConfig config)
    {
        estimatorWatch.Start();
        state.Space = state.Truncated ? null : estimator.Finalize(config.MaxWorkers);
        estimatorWatch.Stop();
        state.EstimatorMs = estimatorWatch.ElapsedMilliseconds;
    }
}

/// <summary>Contract for <see cref="ProfilePlanner"/>. Separate so the run pipeline and the wire
/// handler both depend on the shape rather than the class, and so a test can substitute a plan.</summary>
public interface IProfilePlanner
{
    /// <summary>Streams the work <paramref name="profile"/> implies, source phase then destination
    /// sweep. Faults surface as a single failure item that ends the stream (never an exception
    /// escaping mid-enumeration); cancellation surfaces as <see cref="OperationCanceledException"/>.
    /// <paramref name="state"/> is mutated throughout and holds the plan's totals, truncation flags,
    /// and — once the stream completes — its space projection.</summary>
    IAsyncEnumerable<Result<PlanChunk, string>> PlanAsync(
        Profile profile, string? scopePath, PlanState state,
        DryRunProgressCounters? progress = null, CancellationToken ct = default);
}

/// <summary>The one copy of the async-iterator teardown dance, shared by every layer that drives a
/// chunk stream through an explicit enumerator so it can interleave work between advances.</summary>
internal static class AsyncIteratorTeardown
{
    /// <summary>Makes a phase's teardown safe when the driving iterator is disposed with an advance
    /// still in flight — the normal suspension state at an interleaved yield, and one the IPC server
    /// reaches routinely (a failed frame write on client disconnect disposes the handler without
    /// cancelling its token, which is the server-lifetime token).
    ///
    /// <para>Two hazards, one ordering: disposing a compiler-generated async iterator while its
    /// <c>MoveNextAsync</c> is pending throws (abandoning the underlying pipeline entirely), and the
    /// sweep enumerator's own dispose awaits a producer that only unparks on cancellation. So: cancel
    /// the linked token, then await the pending advance — only after that may the enclosing
    /// <c>await using</c> dispose the enumerator. On every non-abandonment path the advance is
    /// already consumed and this is a completed-task no-op that cancels nothing.</para></summary>
    public static async Task ObserveAbandonedAdvanceAsync(
        Task<bool> pendingAdvance, CancellationTokenSource cts, ILogger logger)
    {
        if (!pendingAdvance.IsCompleted)
            cts.Cancel();
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
            logger.LogDebug(ex, "Chunk-stream advance abandoned during teardown");
        }
    }
}

/// <summary>The other half of that same dance: the poll that keeps a chunk stream's consumer publishing
/// while an advance is pending.
///
/// <para>Necessary because the planner's ENTIRE source walk happens inside the FIRST
/// <c>MoveNextAsync</c> — scan and evaluate are fused and every finding is spooled before a chunk exists
/// — so a consumer that only acts between advances says nothing at all for the longest part of a run.
/// The shared counters are live throughout; this is what gives a consumer somewhere to read them.</para>
///
/// <para>Only the wait is shared, not the sampling: one consumer publishes to an event bus and the other
/// <c>yield return</c>s into its own stream, and a yield cannot cross into a callback. The wait is the
/// half that was subtle enough to be worth having one copy of.</para></summary>
internal static class AsyncIteratorHeartbeat
{
    /// <summary>Waits up to <paramref name="interval"/> for <paramref name="advance"/>. True when the
    /// caller should take a sample; false once the advance has completed or the token has fired — at
    /// which point the caller must fall through to awaiting the advance itself.
    ///
    /// <para>The token check is not redundant with the delay: once <paramref name="ct"/> fires,
    /// <c>Task.Delay(…, ct)</c> completes instantly and a caller that kept polling would spin a core
    /// until the plan finished unwinding, sampling progress nobody will ever see.</para></summary>
    public static async ValueTask<bool> WaitForTickAsync(
        Task<bool> advance, TimeSpan interval, CancellationToken ct)
    {
        if (advance.IsCompleted || ct.IsCancellationRequested)
            return false;
        // Real time, exactly as the pause poll and the drain loop are: this is a display heartbeat and
        // nothing is decided from it, so pinning it to an injected clock would only make the behaviour
        // vanish under a FakeTimeProvider.
        await Task.WhenAny(advance, Task.Delay(interval, ct)).ConfigureAwait(false);
        return !advance.IsCompleted && !ct.IsCancellationRequested;
    }
}
