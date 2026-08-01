using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
using FileManager.Core.Scanning;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using FileManager.Contracts;
using FileManager.Contracts.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>Spec §8: simulates a full run with ZERO filesystem mutation (I-DRYRUN-RO) — every
/// collaborator here is read-only (scan, stat, hash, existence probes). Produces a bipartite plan
/// graph: the source/destination <see cref="PhysicalFile"/> lists are the ground-truth nodes, and
/// the source/destination <see cref="VirtualFileOperation"/> lists are the plan, referencing files
/// by integer index. The two simulate paths differ in ordering: <see cref="SimulateAsync"/> (batched,
/// CLI) sorts candidates by source path so a source file's index is its sorted position and a
/// truncated report is a valid path-ordered prefix; <see cref="SimulateStreamAsync"/> (the GUI) emits
/// findings in discovery order via a spool and leaves the sort to the client, so it never buffers or
/// sorts the whole set. In both, op indices are valid positions into the assembled lists (the global
/// invariant), so the streamed report differs from the batched one only in row order.</summary>
public sealed class DryRunEngine(
    ILogger<DryRunEngine> logger,
    ISourceScanner scanner,
    IFilterCompiler filterCompiler,
    IFileHasher hasher,
    IConflictResolver conflictResolver,
    ISettingsProvider settings,
    TimeProvider time,
    DestinationProjector destinationProjector) : IDryRunEngine
{
    /// <summary>Report size guards: a serialized report must fit an IPC frame (16 MiB cap, §3.1).
    /// The byte budget is the guarantee — each record's size is a cheap tight upper-bound estimate
    /// (<see cref="UpperBoundBytes(DryRunFile)"/> / <see cref="StringUpperBound(string)"/>, never actual
    /// serialization) and the report truncates when the running total would exceed it (16 MiB minus
    /// the response envelope and headroom for future additive fields). Because the estimate is an
    /// upper bound, the true serialized report is always smaller than the budget. The file-count cap
    /// is a secondary bound on UI row-building work.</summary>
    internal const int MaxReportBytes = 12 * 1024 * 1024;
    internal const int MaxReportedFiles = 50_000;

    /// <summary>Per-chunk budget for the streamed path, in the WIRE shape's currency
    /// (<see cref="WireUpperBoundBytes(IPhysicalFileView)"/>) — the same currency the batched
    /// <c>ReportBuilder</c> already measures in.
    ///
    /// <para>The binding constraint is NOT the 16 MiB protocol cap; it is the 85,000-byte Large Object
    /// Heap threshold. Each response is serialized into a fresh exact-size <c>byte[]</c>, so any frame
    /// at or above that lands on the LOH — which is not compacted by default and only collected with a
    /// gen2, i.e. it is a lasting addition to the process's committed footprint rather than a transient
    /// write. At 48 KiB of (deliberately generous) estimate the measured frames land around 25–35 KB,
    /// leaving roughly 2.5–3× headroom even for a directory-per-file tree, whose extra
    /// <c>DryRunDirectory</c> records this estimate does not itself account for.
    /// <c>DryRunFrameSizeTests</c> is what keeps that true.</para>
    ///
    /// <para>The previous 1 MiB was measured against the engine's PRE-normalization shape (a full
    /// absolute Path plus Root at up to 6 bytes/char), which over-counts the wire form — carrying only
    /// a file name plus two ints — by roughly 6–10×. So it produced ~100–170 KB frames: over the LOH
    /// line, and nowhere near the 1 MB it read as.</para></summary>
    internal const int WireChunkByteBudget = 48 * 1024;

    /// <summary>Bytes the disk-backed spool buffers in memory before spilling to a file.
    ///
    /// <para>Deliberately NOT tied to <see cref="WireChunkByteBudget"/>, though it used to share the
    /// chunk constant. They answer different questions: the chunk budget bounds one IPC frame, this
    /// bounds the spool's pre-spill buffer. Pinning this to 48 KiB would send almost every run —
    /// including small ones that comfortably fit in memory today — through a scratch file, which is a
    /// separate trade needing its own measurement rather than a side effect of a frame-size fix.</para></summary>
    internal const int SpoolSpillThresholdBytes = 1 * 1024 * 1024;

    /// <summary>Streaming lifts the frame-cap ceiling, but not the good sense of an upper limit. The
    /// evaluated results (<see cref="ScanAndEvaluateAsync"/>) have to be fully materialized to sort
    /// by source path before the first chunk, so a pathological scan of millions of files would
    /// otherwise buffer — and hash — all of them. This bound caps the accepted candidates (and
    /// therefore the evaluation work), the same way <see cref="MaxReportedFiles"/> bounds the
    /// batched path. Hitting it marks the report truncated.</summary>
    internal const int MaxStreamedFiles = 500_000;

    /// <summary>Bound on the scan → evaluation hand-off buffer (see
    /// <see cref="ScanAndEvaluateAsync"/>): big enough that a bursty scan never starves the
    /// evaluation workers, small enough that a huge tree cannot balloon memory ahead of them
    /// (the scanner's own output buffer is 4096).</summary>
    private const int PipelineBufferCapacity = 1024;

    /// <summary>Test seam: production uses <see cref="MaxReportBytes"/>; tests shrink it so
    /// truncation is reachable without tens of thousands of real files.</summary>
    internal int ReportByteBudget { get; init; } = MaxReportBytes;

    /// <summary>Test seam: production uses <see cref="WireChunkByteBudget"/>; tests shrink it so a
    /// stream splits into several chunks without generating a megabyte of files.</summary>
    internal int ChunkByteBudget { get; init; } = WireChunkByteBudget;

    /// <summary>Test seam: production uses <see cref="MaxStreamedFiles"/>; tests shrink it so the
    /// candidate cap is reachable without generating half a million files.</summary>
    internal int MaxScannedCandidates { get; init; } = MaxStreamedFiles;

    /// <summary>Test seam: production uses <see cref="MaxReportedFiles"/>; tests shrink it so the
    /// batched file cap is reachable without generating tens of thousands of files.</summary>
    internal int MaxBatchCandidates { get; init; } = MaxReportedFiles;

    /// <summary>Test seam: the spool the streamed path writes findings to. Null (production) builds a
    /// disk-backed <see cref="FileDryRunSpool"/> from the current scratch-directory setting; tests set
    /// an <see cref="InMemoryDryRunSpoolFactory"/> to stay off disk.</summary>
    internal IDryRunSpoolFactory? SpoolFactory { get; init; }

    /// <summary>Test seam: bytes buffered in memory before the disk-backed spool spills to a file.
    /// Production uses <see cref="SpoolSpillThresholdBytes"/>; tests shrink it to force a spill.</summary>
    internal long SpillThresholdBytes { get; init; } = SpoolSpillThresholdBytes;

    /// <summary>Test seam: the carrier pool the streamed read-back rents from. Null (production) builds
    /// a fresh pool per run; tests inject one so they can assert every rented carrier was recycled
    /// (borrowed == returned) after a spilled run.</summary>
    internal EvaluationCarrierPool? StreamCarrierPool { get; init; }

    /// <summary>The per-run spool for the streamed path: the injected test factory, else a disk-backed
    /// spool that spills to <see cref="GlobalSettings.ScratchDirectory"/> past
    /// <see cref="SpillThresholdBytes"/> (small runs stay in memory). The disk-backed spool shares the
    /// run's <paramref name="pool"/> so its spilled read-back fills recycled carriers; an injected
    /// factory (tests use the in-memory spool) ignores the pool and replays original records.</summary>
    private IDryRunSpool CreateSpool(EvaluationCarrierPool pool) =>
        SpoolFactory?.Create(pool)
        ?? new FileDryRunSpool(settings.Current.ScratchDirectory, SpillThresholdBytes, pool, logger);

    /// <summary>Previews the given profile object directly (a persisted profile the handler resolved
    /// from the catalog, or an unsaved in-memory draft). All collaborators remain read-only
    /// (I-DRYRUN-RO).</summary>
    public async Task<Result<DryRunReport, string>> SimulateAsync(
        Profile profile, string? scopePath, CancellationToken ct = default)
    {
        DateTimeOffset startedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Dry-run started for profile {ProfileId} (scope {Scope})",
            profile.Id, scopePath ?? "<all sources>");

        // One compiled set per source, keyed by the source root the scanner stamps on payloads.
        Result<Dictionary<string, CompiledFilterSet>, string> filtersResult = CompileFilters(profile);
        if (filtersResult.TryGetError(out string? compileError))
            return compileError;
        filtersResult.TryGetValue(out Dictionary<string, CompiledFilterSet>? filtersBySourceRoot);

        bool hasTransformers = profile.Transformers is { Count: > 0 };

        // Phases 1+2 are fused (see ScanAndEvaluateAsync): the scan streams payloads into a bounded
        // buffer while a worker pool evaluates them as they arrive, so the two dominant I/O costs
        // overlap instead of running back-to-back. A fatal fault aborts the whole run; warnings are
        // logged and skipped; the file cap bounds the accepted payloads and therefore the evaluation
        // work. A hash or target loop cut short by cancellation returns a partial/placeholder result;
        // cancellation turns the whole run into Canceled rather than a misleading partial report.
        RunCounters counters = new();
        // The batched path collects the whole evaluated set, then sorts by source path (below): a
        // source file's index is its sorted position, so a byte-truncated report stays a valid
        // path-ordered prefix. The streamed path instead spools findings in discovery order and lets
        // the client sort (see SimulateStreamAsync).
        ConcurrentQueue<FileEvaluation> results = new();
        PipelineOutcome outcome;
        try
        {
            outcome = await ScanAndEvaluateAsync(
                profile, scopePath, filtersBySourceRoot!, hasTransformers, MaxBatchCandidates, counters,
                progress: null, (evaluation, _) => { results.Enqueue(evaluation); return ValueTask.CompletedTask; }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<DryRunReport, string>.Canceled();
        }

        if (outcome.FatalScanError is not null)
        {
            logger.LogError("Dry-run for profile {ProfileId} failed: scan error: {Message}",
                profile.Id, outcome.FatalScanError);
            return $"scan failed: {outcome.FatalScanError}";
        }

        bool truncated = outcome.ScanTruncated;

        // Fix the output order by sorting the evaluations by source path. A source file's index is
        // its sorted position, so truncation keeps a path-ordered *prefix* and no retained operation
        // references a dropped file. The byte budget can only be applied here (assembly needs the
        // sorted order), so a byte-truncated report has over-evaluated its dropped tail — bounded by
        // the file cap, and accepted as the price of the scan/eval overlap.
        List<FileEvaluation> evaluations = [.. results];
        evaluations.Sort(static (a, b) =>
            string.Compare(a.SourceFile.Path, b.SourceFile.Path, StringComparison.OrdinalIgnoreCase));

        ReportBuilder builder = new(ReportByteBudget);
        foreach (FileEvaluation evaluation in evaluations)
        {
            if (!builder.TryAddBundle(evaluation))
                break;      // budget full — Truncated is set
        }

        truncated |= builder.Truncated;

        if (truncated)
            logger.LogWarning(
                "Dry-run report for profile {ProfileId} truncated at {FileCount} source files / ~{Bytes:N0} bytes " +
                "(caps: {ByteCap:N0} bytes, {FileCap} files) — the scan found more",
                profile.Id, builder.SourceFiles.Count, builder.ReportBytes, ReportByteBudget, MaxBatchCandidates);

        // Phase 3: destination-only entries (pre-existing Untouched + Mirror orphans). Suppressed
        // entirely when the source pass truncated (the survivor set would be incomplete, so any
        // orphan classification is untrustworthy). Appended only while their size keeps the report
        // under budget; an overflow drops the rest and marks the report truncated.
        // AdditiveArchive can skip the sweep when the profile opts out (ScanDestination = false);
        // Mirror always sweeps because the sweep is its only source of Deleted-orphan previews.
        // The byte budget below bounds the REPORT, not the sweep: without a cap passed in, the projector
        // first materializes one PhysicalFile plus one VirtualFileOperation for every pre-existing file
        // under every target root, on the service's heap, and only then does TryAddSweepEntry start
        // rejecting. A target that is a large existing archive could therefore exhaust service memory —
        // the exact failure the streamed path was hardened against (DryRunStreamHandler bounds its sweep
        // by the same overall file budget). Same shape here, using this path's own candidate cap.
        bool scanDestinations = profile.EffectiveScanDestination;
        int sweepBudget = Math.Max(0, MaxBatchCandidates - builder.DestinationFiles.Count);
        DestinationSweepResult sweep = scanDestinations
            ? destinationProjector.Sweep(profile, builder.Survivors, truncated, ct, sweepBudget)
            : new DestinationSweepResult([], []);
        if (sweep.Truncated)
        {
            logger.LogWarning(
                "Dry-run destination sweep for profile {ProfileId} hit the {Cap:N0}-entry bound; report truncated",
                profile.Id, MaxBatchCandidates);
            truncated = true;
        }
        for (int i = 0; i < sweep.Files.Count; i++)
        {
            if (!builder.TryAddSweepEntry(sweep.Files[i], sweep.Ops[i]))
            {
                truncated = true;
                logger.LogWarning(
                    "Dry-run report for profile {ProfileId} truncated its destination entries " +
                    "(byte cap {ByteCap:N0}) — more pre-existing/orphan files exist",
                    profile.Id, ReportByteBudget);
                break;
            }
        }

        DateTimeOffset completedAt = time.GetUtcNow();
        logger.LogInformation(
            "Dry-run completed for profile {ProfileId}: {SourceCount} source files, {DestCount} destination files " +
            "in {ElapsedMs}ms{Truncated} (scan {ScanMs}ms within pipeline {EvalMs}ms — the phases overlap; " +
            "{Probes} existence probes, {HashCount} files hashed / {HashBytes:N0} bytes)",
            profile.Id, builder.SourceFiles.Count, builder.DestinationFiles.Count,
            (completedAt - startedAt).TotalMilliseconds, truncated ? " (report truncated)" : "",
            outcome.ScanMs, outcome.EvalMs,
            counters.ExistenceProbes, counters.FilesHashed, counters.BytesHashed);

        return new DryRunReport
        {
            ProfileId = profile.Id,
            GeneratedAt = completedAt,
            Directories = builder.Directories,
            SourceFiles = builder.SourceFiles,
            DestinationFiles = builder.DestinationFiles,
            SourceOperations = builder.SourceOperations,
            DestinationOperations = builder.DestinationOperations,
            Truncated = truncated,
        };
    }

    /// <summary>Streaming counterpart to <see cref="SimulateAsync"/> (spec §8): yields the per-source-file
    /// phase as a sequence of chunks instead of one bounded, truncatable report, so a run larger than
    /// the single-frame budget reports every file. Reuses the same fused scan+evaluate pipeline, but
    /// spools each finding in discovery (completion) order — no global sort — so the whole evaluated
    /// set is never buffered (the spool spills to disk past a threshold); the replay is buffered into a
    /// chunk and flushed once its cheap upper-bound size crosses <see cref="WireChunkByteBudget"/>.
    /// Indices are assigned globally across chunks so the consumer can concatenate them; the client
    /// sorts the concatenated report for display. The destination sweep is left to the handler (which
    /// runs it after the file phase). A fatal setup/scan error is a single failure item that ends the
    /// stream; cancellation surfaces as <see cref="OperationCanceledException"/> from the enumerator.</summary>
    /// <summary>Previews the given profile object directly (a persisted profile the handler resolved
    /// from the catalog, or an unsaved in-memory draft). All collaborators remain read-only
    /// (I-DRYRUN-RO); catalog membership was never what enforced that.</summary>
    public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
        Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTimeOffset startedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Dry-run (stream) started for profile {ProfileId} (scope {Scope})",
                profile.Id, scopePath ?? "<all sources>");

        Result<Dictionary<string, CompiledFilterSet>, string> filtersResult = CompileFilters(profile);
        if (filtersResult.TryGetError(out string? compileError))
        {
            yield return compileError;
            yield break;
        }
        filtersResult.TryGetValue(out Dictionary<string, CompiledFilterSet>? filtersBySourceRoot);

        bool hasTransformers = profile.Transformers is { Count: > 0 };

        // Phases 1+2 are fused (see ScanAndEvaluateAsync): the scan streams payloads into a bounded
        // buffer while a worker pool evaluates them as they arrive. Each finding is spooled in
        // discovery (completion) order as it is produced — NO global sort — so the whole evaluated set
        // is never held in memory at once (the spool spills to disk past a threshold) and the scan can
        // finish and release its filesystem handles before the client has consumed anything. The
        // client sorts for display (DryRunViewModel), which is the only place the order matters. A
        // fatal fault ends the stream with a failure; cancellation propagates as an
        // OperationCanceledException from the enumerator, exactly as before.
        RunCounters counters = new();
        // One carrier pool per run, shared with the spool (rents on a spilled read-back) and recycled
        // here per chunk. Dropped with the run — no cross-run retention. A test may inject one to
        // assert borrowed == returned.
        EvaluationCarrierPool pool = StreamCarrierPool ?? new();
        await using IDryRunSpool spool = CreateSpool(pool);
        PipelineOutcome? outcome = null;
        string? spoolError = null;
        try
        {
            outcome = await ScanAndEvaluateAsync(
                profile, scopePath, filtersBySourceRoot!, hasTransformers, MaxScannedCandidates, counters,
                progress, (evaluation, token) => spool.WriteAsync(evaluation, token), ct)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            // The spool writer faulted (disk full, unwritable scratch directory) and failed its
            // channel to unblock the workers; the root cause rides in as the inner exception. A fatal
            // stream error is reported as a failure item (like scan faults), never a torn stream.
            spoolError = (ex.InnerException ?? ex).Message;
        }

        if (spoolError is null && outcome!.FatalScanError is not null)
        {
            logger.LogError("Dry-run (stream) for profile {ProfileId} failed: scan error: {Message}",
                profile.Id, outcome.FatalScanError);
            yield return $"scan failed: {outcome.FatalScanError}";
            yield break;
        }

        bool scanTruncated = outcome?.ScanTruncated ?? false;
        if (scanTruncated)
            logger.LogWarning(
                "Dry-run (stream) for profile {ProfileId} hit the {Cap:N0}-candidate safety bound; " +
                "report truncated — the scan found more",
                profile.Id, MaxScannedCandidates);

        if (spoolError is null)
        {
            // Surfaces a writer fault the pipeline outran (small run: nothing ever blocked on the
            // failed channel, so ScanAndEvaluateAsync completed normally).
            try { await spool.CompleteWritingAsync().ConfigureAwait(false); }
            catch (IOException ex) { spoolError = ex.Message; }
        }

        if (spoolError is not null)
        {
            logger.LogError("Dry-run (stream) for profile {ProfileId} failed: snapshot spool error: {Message}",
                profile.Id, spoolError);
            yield return $"dry-run spool failed: {spoolError}";
            yield break;
        }

        // Replay the spool in discovery order, flushing a chunk whenever the buffer's upper-bound size
        // crosses the threshold. Indices are global across chunks (tracked by the accumulator) so the
        // client concatenates the chunks, then sorts them for display.
        StreamAccumulator accumulator = new();
        bool anyEmitted = false;
        // Manual enumeration (not await foreach): a read-side snapshot fault — truncated length
        // prefix, implausible record, corrupted payload — must become the same single failure item
        // the write side produces, never a torn stream. yield cannot live inside a catch, so the
        // MoveNext runs in a try and the yields stay outside it.
        string? replayError = null;
        await using (IAsyncEnumerator<IEvaluationView> replay = spool.ReadAllAsync(ct).GetAsyncEnumerator(ct))
        {
            while (true)
            {
                IEvaluationView evaluation;
                try
                {
                    if (!await replay.MoveNextAsync().ConfigureAwait(false))
                        break;
                    evaluation = replay.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;   // cancellation keeps its enumerator contract
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Dry-run (stream) for profile {ProfileId} failed: snapshot replay error: {Message}",
                        profile.Id, ex.Message);
                    replayError = ex.Message;
                    break;
                }

                accumulator.Add(evaluation);
                if (accumulator.Bytes >= ChunkByteBudget)
                {
                    (DryRunChunk chunk, List<IEvaluationView> recyclables) = accumulator.Flush();
                    yield return Result<DryRunChunk, string>.Success(chunk with { ScanTruncated = scanTruncated });
                    // I-POOL-RECYCLE: the yield has resumed, so the handler has fully consumed this chunk
                    // (frames serialize synchronously before the next MoveNext) — its carriers are safe to
                    // return. A no-op for original records; recycles carriers for a spilled run.
                    Recycle(recyclables);
                    anyEmitted = true;
                }
            }
        }
        if (replayError is not null)
        {
            yield return $"dry-run spool failed: {replayError}";
            yield break;
        }
        if (accumulator.HasData)
        {
            (DryRunChunk chunk, List<IEvaluationView> recyclables) = accumulator.Flush();
            yield return Result<DryRunChunk, string>.Success(chunk with { ScanTruncated = scanTruncated });
            Recycle(recyclables);
            anyEmitted = true;
        }

        // Guarantee the truncation signal reaches the handler even when no data chunk carried it
        // (e.g. every candidate was filtered out) — otherwise the handler would sweep an incomplete
        // survivor set and could preview bogus Mirror deletions.
        if (scanTruncated && !anyEmitted)
            yield return Result<DryRunChunk, string>.Success(new DryRunChunk([], [], [], [], ScanTruncated: true));

        DateTimeOffset completedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Dry-run (stream) completed for profile {ProfileId}: {SourceCount} source files in {ElapsedMs}ms " +
                "(scan {ScanMs}ms within pipeline {EvalMs}ms — the phases overlap; {Probes} existence probes, " +
                "{HashCount} files hashed / {HashBytes:N0} bytes)",
                profile.Id, accumulator.TotalSourceFiles, (completedAt - startedAt).TotalMilliseconds,
                outcome!.ScanMs, outcome.EvalMs,
                counters.ExistenceProbes, counters.FilesHashed, counters.BytesHashed);
    }

    /// <summary>Returns a flushed chunk's entries to the carrier pool. A no-op for original records
    /// (in-memory / non-spilled replay); recycles the carrier graph for a spilled run. Called only
    /// after the chunk's <c>yield return</c> has resumed — the consumer is provably done with it.</summary>
    private static void Recycle(List<IEvaluationView> entries)
    {
        foreach (IEvaluationView entry in entries)
            entry.Recycle();
    }

    /// <summary>The fused scan+evaluation pipeline's outcome (metadata only). Each evaluated finding
    /// was handed to the caller's sink as it completed — in the parallel scan's non-deterministic
    /// completion order — so the batched path collects+sorts them and the streamed path spools them.
    /// A fatal scan fault is carried as a value in <see cref="FatalScanError"/>, never thrown.
    /// <see cref="ScanMs"/> is the pump's span within the <see cref="EvalMs"/> pipeline wall time —
    /// the two overlap by design.</summary>
    private sealed record PipelineOutcome(
        bool ScanTruncated,
        string? FatalScanError,
        long ScanMs,
        long EvalMs);

    /// <summary>Fused scan → evaluation pipeline shared by the batched and streamed simulations
    /// (spec §8; all collaborators read-only, I-DRYRUN-RO). A single pump task drains the scanner —
    /// its returned iterator is not safe for concurrent MoveNext — into a bounded channel while
    /// <see cref="ResolveWorkers"/> consumers evaluate payloads as they arrive, so enumeration and
    /// evaluation I/O overlap instead of running back-to-back. Responsibilities and semantics,
    /// unchanged from the pre-pipeline phases:
    /// <list type="bullet">
    /// <item>Warning fault → logged and skipped (in the pump).</item>
    /// <item>Fatal fault → the pump cancels the linked token (consumers tear down promptly, the
    /// buffered tail is dropped unevaluated) and the fault comes back as a value. Checked before
    /// cancellation on the way out, so a fault that raced a cancel still wins deterministically.</item>
    /// <item>File cap (<paramref name="candidateCap"/>) → stop accepting, mark truncated. As before,
    /// a fatal walk-root fault produced after the cap goes unobserved and the run resolves as
    /// truncated-success (see ISourceScanner.Scan remarks).</item>
    /// <item>Cancellation (the caller's token) → OperationCanceledException after both sides have
    /// torn down; the scan iterator's disposal runs the scanner's own cancel → drain → dispose.</item>
    /// <item>An unexpected evaluation exception → cancels the pump (it must not stay blocked on a
    /// full buffer), then rethrows once both sides have joined.</item>
    /// </list></summary>
    private async Task<PipelineOutcome> ScanAndEvaluateAsync(
        Profile profile,
        string? scopePath,
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot,
        bool hasTransformers,
        int candidateCap,
        RunCounters counters,
        DryRunProgressCounters? progress,
        Func<FileEvaluation, CancellationToken, ValueTask> sink,
        CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Channel<Payload> channel = Channel.CreateBounded<Payload>(new BoundedChannelOptions(PipelineBufferCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Written by the pump before it completes or cancels; read only after the pump is awaited
        // (the task join is the memory fence).
        string? fatalError = null;
        bool scanTruncated = false;

        Stopwatch scanWatch = Stopwatch.StartNew();

        Task pump = Task.Run(async () =>
        {
            try
            {
                int accepted = 0;
                foreach (var scanned in scanner.Scan(profile, TriggerKind.Cli, scopePath, linked.Token))
                {
                    if (scanned.TryGetError(out EnumerationFault fault))
                    {
                        if (fault.Severity == EnumerationSeverity.Fatal)
                        {
                            fatalError = fault.Message;
                            linked.Cancel();
                            return;
                        }
                        // Counted as well as logged: the report has no field for "this tree was walked
                        // incompletely", so the count is what lets the caller say so.
                        progress?.EntrySkipped();
                        logger.LogWarning("Dry-run enumeration warning: {Message}", fault.Message);
                        continue;
                    }

                    if (accepted >= candidateCap)
                    {
                        scanTruncated = true;
                        return;
                    }

                    scanned.TryGetValue(out Payload? payload);
                    await channel.Writer.WriteAsync(payload!, linked.Token).ConfigureAwait(false);
                    accepted++;
                    progress?.SourceDiscovered();
                }
            }
            catch (OperationCanceledException)
            {
                throw;   // teardown — the join disambiguates cancellation from a fatal fault
            }
            catch (Exception ex)
            {
                // Last-resort catch-all at this task boundary (directive): an unexpected throw from
                // the scan iterator (outside its Result protocol) must become the documented single
                // failure item, not a raw exception tearing the streaming enumerator.
                logger.LogError(ex, "Dry-run scan pump failed unexpectedly for profile {ProfileId}", profile.Id);
                fatalError = $"unexpected scan error: {ex.Message}";
                linked.Cancel();
            }
            finally
            {
                // Unconditional, so consumers never wait on a writer that is already gone. Leaving
                // the foreach on any path disposes the scan iterator, which runs the scanner's own
                // teardown (cancel producers → drain → dispose).
                channel.Writer.TryComplete();
                scanWatch.Stop();
            }
        });

        Stopwatch evalWatch = Stopwatch.StartNew();
        Exception? consumerError = null;
        try
        {
            await Parallel.ForEachAsync(
                channel.Reader.ReadAllAsync(linked.Token),
                new ParallelOptions { MaxDegreeOfParallelism = ResolveWorkers(), CancellationToken = linked.Token },
                async (payload, token) =>
                {
                    FileEvaluation? evaluation = await EvaluateFileAsync(
                        profile, payload, filtersBySourceRoot, hasTransformers, counters, token)
                        .ConfigureAwait(false);
                    if (evaluation is not null)
                        await sink(evaluation, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Fatal-fault teardown or user cancellation — disambiguated after the pump joins.
        }
        catch (Exception ex)
        {
            consumerError = ex;
            linked.Cancel();   // unblock a pump stuck writing into a full buffer
        }
        evalWatch.Stop();

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on every teardown path: the user's token tripping the scan, or a fault above
            // cancelling the linked source while the pump was blocked in MoveNext/WriteAsync.
        }

        if (fatalError is not null)
            return new PipelineOutcome(false, fatalError, scanWatch.ElapsedMilliseconds, evalWatch.ElapsedMilliseconds);
        if (consumerError is not null)
            ExceptionDispatchInfo.Capture(consumerError).Throw();
        ct.ThrowIfCancellationRequested();

        return new PipelineOutcome(
            scanTruncated, null, scanWatch.ElapsedMilliseconds, evalWatch.ElapsedMilliseconds);
    }

    /// <summary>Compiles one filter set per source, keyed by the source root the scanner stamps on
    /// payloads. Shared by the batched and streamed simulations.</summary>
    private Result<Dictionary<string, CompiledFilterSet>, string> CompileFilters(Profile profile)
    {
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceConfig source in profile.Sources)
        {
            var compiled = filterCompiler.Compile(profile.Filters, source.Filters);
            if (compiled.TryGetError(out string? compileError))
            {
                logger.LogError(
                    "Dry-run for profile {ProfileId} failed: filter compilation error (was this profile saved through validation?): {Error}",
                    profile.Id, compileError);
                return $"filter compilation failed (was this profile saved through validation?): {compileError}";
            }

            compiled.TryGetValue(out CompiledFilterSet? set);
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                filtersBySourceRoot[root.Value] = set!;
        }
        return filtersBySourceRoot;
    }

    /// <summary>Resolves the evaluation (hash) worker count for this run from the global scan-threading
    /// settings (profiles no longer override concurrency). Clamped to at least 1 so
    /// <see cref="ParallelOptions.MaxDegreeOfParallelism"/> is always valid.</summary>
    internal int ResolveWorkers() => ScanThreadResolver.ResolveMaxHashThreads(settings.Current.ScanThreading);

    /// <summary>Per-run I/O accounting for the evaluation phase, surfaced in the completion log so a
    /// slow run attributes its time to a phase without a profiler. Incremented under
    /// <see cref="Parallel.ForEachAsync"/>, so all writes are interlocked.</summary>
    private sealed class RunCounters
    {
        private long _existenceProbes;
        private long _filesHashed;
        private long _bytesHashed;

        public long ExistenceProbes => Interlocked.Read(ref _existenceProbes);
        public long FilesHashed => Interlocked.Read(ref _filesHashed);
        public long BytesHashed => Interlocked.Read(ref _bytesHashed);

        // No separate existing-target-stat counter: the metadata read IS the existence probe (see the
        // comment at the FileMetadataReader.Read call), so counting it twice would double-count one stat.
        public void CountExistenceProbe() => Interlocked.Increment(ref _existenceProbes);

        public void CountHash(long bytes)
        {
            Interlocked.Increment(ref _filesHashed);
            Interlocked.Add(ref _bytesHashed, bytes);
        }
    }

    /// <summary>Batched-report accumulator with truncation to the serialized byte budget, fed bundles
    /// in final (source-path) order. Emits the normalized wire shape: records carry
    /// (DirIndex, FileName) into a shared directory table built as bundles arrive. Sizing uses the
    /// tight upper-bound estimate only (see <see cref="StringUpperBound"/>) — a true bound including
    /// the directory entries a unit first references, so the serialized report always fits the
    /// budget; near the cap it may truncate a handful of records earlier than exact measurement
    /// would, never later. A unit is added atomically: a rejected unit's freshly-added directory
    /// entries are rolled back, so a retained record never references a dropped table entry and the
    /// table carries no unused tail.</summary>
    private sealed class ReportBuilder(long budget)
    {
        private readonly DryRunDirectoryTableBuilder _dirs = new();

        public IReadOnlyList<DryRunDirectory> Directories => _dirs.Entries;
        public List<DryRunFile> SourceFiles { get; } = [];
        public List<DryRunFile> DestinationFiles { get; } = [];
        public List<DryRunOperation> SourceOperations { get; } = [];
        public List<DryRunOperation> DestinationOperations { get; } = [];

        /// <summary>Destination paths a kept source writes to, accumulated from the stringy bundles —
        /// the batched path's input to the destination sweep (mirrors the streaming handler's
        /// running survivor set).</summary>
        public HashSet<NormalizedPath> Survivors { get; } = [];

        /// <summary>True once a unit was rejected because keeping it would cross the budget.</summary>
        public bool Truncated { get; private set; }

        /// <summary>The running upper-bound estimate.</summary>
        public long ReportBytes => _total;

        private long _total;

        /// <summary>Adds a whole per-file bundle atomically, remapping bundle-local indices to global
        /// positions with the same arithmetic the streamed <see cref="StreamAccumulator"/> applies.
        /// Returns false (and sets <see cref="Truncated"/>) without keeping anything if it would cross
        /// the budget.</summary>
        public bool TryAddBundle(FileEvaluation bundle)
        {
            int mark = _dirs.Mark();
            DryRunFile sourceFile = _dirs.Convert(bundle.SourceFile);
            DryRunOperation sourceOp = _dirs.Convert(bundle.SourceOp);
            List<DryRunFile> destFiles = new(bundle.DestinationFiles.Count);
            foreach (PhysicalFile file in bundle.DestinationFiles)
                destFiles.Add(_dirs.Convert(file));
            List<DryRunOperation> destOps = new(bundle.DestinationOps.Count);
            foreach (VirtualFileOperation op in bundle.DestinationOps)
                destOps.Add(_dirs.Convert(op));

            long bytes = UpperBoundBytes(sourceFile) + UpperBoundBytes(sourceOp) + NewDirectoryBytes(mark);
            foreach (DryRunFile file in destFiles)
                bytes += UpperBoundBytes(file);
            foreach (DryRunOperation op in destOps)
                bytes += UpperBoundBytes(op);
            if (!TryReserve(bytes))
            {
                _dirs.RollbackTo(mark);
                return false;
            }

            int sourceIndex = SourceFiles.Count;
            int destBase = DestinationFiles.Count;
            SourceFiles.Add(sourceFile);
            SourceOperations.Add(sourceOp with { SourceIndex = sourceIndex });
            foreach (DryRunFile file in destFiles)
                DestinationFiles.Add(file);
            foreach (DryRunOperation op in destOps)
                DestinationOperations.Add(op with
                {
                    SourceIndex = op.SourceIndex == 0 ? sourceIndex : -1,
                    SubjectIndex = op.SubjectIndex >= 0 ? destBase + op.SubjectIndex : -1,
                });
            DestinationProjector.AccumulateSurvivors(Survivors, bundle.DestinationOps);
            return true;
        }

        /// <summary>Adds one swept (file, op) pair atomically, wiring the op's SubjectIndex to the
        /// file's new position.</summary>
        public bool TryAddSweepEntry(PhysicalFile file, VirtualFileOperation op)
        {
            int mark = _dirs.Mark();
            DryRunFile wireFile = _dirs.Convert(file);
            DryRunOperation wireOp = _dirs.Convert(op);
            if (!TryReserve(UpperBoundBytes(wireFile) + UpperBoundBytes(wireOp) + NewDirectoryBytes(mark)))
            {
                _dirs.RollbackTo(mark);
                return false;
            }
            int subjectIndex = DestinationFiles.Count;
            DestinationFiles.Add(wireFile);
            DestinationOperations.Add(wireOp with { SubjectIndex = subjectIndex });
            return true;
        }

        /// <summary>The serialized upper bound of the directory entries added since
        /// <paramref name="mark"/> — a unit pays for the table entries it first references.</summary>
        private long NewDirectoryBytes(int mark)
        {
            long bytes = 0;
            for (int i = mark; i < _dirs.Count; i++)
                bytes += DirectoryStructuralBytes + StringUpperBound(_dirs.Entries[i].Name);
            return bytes;
        }

        private bool TryReserve(long upperBound)
        {
            if (_total + upperBound > budget)
            {
                Truncated = true;
                return false;
            }
            _total += upperBound;
            return true;
        }
    }

    /// <summary>Streaming accumulator: buffers one chunk's entries while tracking global source/dest
    /// counts across all chunks, so operation indices stay valid positions into the fully assembled
    /// report. Sizing uses the cheap upper-bound estimate only (a chunk just needs to sit under the
    /// frame cap).
    /// <para>The chunk lists hold the read-only view, so an entry's file/op objects — original records
    /// or pooled carriers — flow through untouched (no copy). The global-index remap is the one place
    /// the two entry kinds diverge: an original record is immutable, so its ops are copied via
    /// <c>record with { }</c>; a pooled carrier is mutated in place (that in-place remap is the whole
    /// reason the spilled read path is backed by carriers). The arithmetic is identical either way.
    /// <see cref="Flush"/> also returns the chunk's entries so the engine can recycle any pooled
    /// carriers once the chunk is consumed.</para></summary>
    private sealed class StreamAccumulator
    {
        private List<IPhysicalFileView> _sourceFiles = [];
        private List<IPhysicalFileView> _destinationFiles = [];
        private List<IFileOperationView> _sourceOps = [];
        private List<IFileOperationView> _destinationOps = [];
        private List<IEvaluationView> _entries = [];
        private int _globalSourceCount;
        private int _globalDestCount;

        public long Bytes { get; private set; }
        public int TotalSourceFiles => _globalSourceCount;
        public bool HasData => _sourceFiles.Count > 0 || _destinationFiles.Count > 0;

        public void Add(IEvaluationView bundle)
        {
            int sourceIndex = _globalSourceCount;
            int destBase = _globalDestCount;

            // Files carry no bundle-local index, so the view references append unchanged for either
            // entry kind.
            _sourceFiles.Add(bundle.SourceFile);
            foreach (IPhysicalFileView file in bundle.DestinationFiles)
                _destinationFiles.Add(file);

            // Ops: remap bundle-local indices to global positions. Same arithmetic the batched
            // ReportBuilder applies; records copy (immutable), carriers mutate in place.
            switch (bundle)
            {
                case FileEvaluation original:
                    _sourceOps.Add(original.SourceOp with { SourceIndex = sourceIndex });
                    foreach (VirtualFileOperation op in original.DestinationOps)
                        _destinationOps.Add(op with
                        {
                            SourceIndex = op.SourceIndex == 0 ? sourceIndex : -1,
                            SubjectIndex = op.SubjectIndex >= 0 ? destBase + op.SubjectIndex : -1,
                        });
                    break;
                case PooledEvaluation pooled:
                    pooled.SourceOp.SourceIndex = sourceIndex;
                    _sourceOps.Add(pooled.SourceOp);
                    foreach (PooledFileOperation op in pooled.DestinationOps)
                    {
                        int subject = op.SubjectIndex;
                        op.SourceIndex = op.SourceIndex == 0 ? sourceIndex : -1;
                        op.SubjectIndex = subject >= 0 ? destBase + subject : -1;
                        _destinationOps.Add(op);
                    }
                    break;
            }

            _globalSourceCount += 1;
            _globalDestCount += bundle.DestinationFiles.Count;
            Bytes += BundleUpperBound(bundle);
            _entries.Add(bundle);
        }

        /// <summary>Seals the current chunk and resets for the next. Returns the chunk plus the entries
        /// that went into it — the engine recycles those after the chunk's <c>yield</c> resumes so any
        /// pooled carriers return to the pool (a no-op for original records).</summary>
        public (DryRunChunk Chunk, List<IEvaluationView> Entries) Flush()
        {
            DryRunChunk chunk = new(_sourceFiles, _destinationFiles, _sourceOps, _destinationOps);
            List<IEvaluationView> entries = _entries;
            _sourceFiles = [];
            _destinationFiles = [];
            _sourceOps = [];
            _destinationOps = [];
            _entries = [];
            Bytes = 0;
            return (chunk, entries);
        }
    }

    // Fixed structural overhead (braces, property names, enum text, numeric fields, quotes) — generous
    // so it is a true upper bound alongside the 6-bytes-per-char string bound. The file/operation
    // constants predate the normalized wire shape (whose records swap two path strings for two ints)
    // and stay generous for it; the directory constant covers a DryRunDirectory's name-plus-parent
    // envelope.
    private const int PhysicalFileStructuralBytes = 256;
    private const int OperationStructuralBytes = 320;
    private const int DirectoryStructuralBytes = 64;

    private static long BundleUpperBound(IEvaluationView bundle)
    {
        long bytes = WireUpperBoundBytes(bundle.SourceFile) + WireUpperBoundBytes(bundle.SourceOp);
        foreach (IPhysicalFileView f in bundle.DestinationFiles) bytes += WireUpperBoundBytes(f);
        foreach (IFileOperationView o in bundle.DestinationOps) bytes += WireUpperBoundBytes(o);
        return bytes;
    }

    /// <summary>The serialized upper bound of what an engine-shape file will cost ON THE WIRE, i.e.
    /// after <c>DryRunDirectoryTableBuilder</c> normalizes it into a <see cref="DryRunFile"/>: the
    /// directory and root collapse to two ints in a shared table, so the only string left is the file
    /// name. Measuring the full <c>Path</c> and <c>Root</c> instead — as the streamed path used to —
    /// over-counts a ~90-char path by roughly 6–10× and silently turns a nominal budget into frames
    /// several times smaller than intended.
    ///
    /// <para>What this does NOT count is the <see cref="DryRunDirectory"/> entries a chunk first
    /// references; the streamed accumulator does not own the directory table (the handler converts
    /// after chunking). <see cref="WireChunkByteBudget"/>'s headroom is what absorbs those, and
    /// <c>DryRunFrameSizeTests</c> exercises the directory-per-file worst case.</para></summary>
    internal static long WireUpperBoundBytes(IPhysicalFileView file) =>
        PhysicalFileStructuralBytes + StringUpperBound(Path.GetFileName(file.Path.AsSpan()));

    /// <inheritdoc cref="WireUpperBoundBytes(IPhysicalFileView)"/>
    internal static long WireUpperBoundBytes(IFileOperationView op) =>
        OperationStructuralBytes + StringUpperBound(Path.GetFileName(op.Path.AsSpan())) + StringUpperBound(op.Detail);

    private static long UpperBoundBytes(DryRunFile file) =>
        PhysicalFileStructuralBytes + StringUpperBound(file.FileName);

    private static long UpperBoundBytes(DryRunOperation op) =>
        OperationStructuralBytes + StringUpperBound(op.FileName) + StringUpperBound(op.Detail);

    // Which ASCII chars the serializer's encoder (JavaScriptEncoder.Default — the JSON context
    // sets no custom encoder) writes through verbatim, built from the encoder itself so the table
    // can never disagree with the actual escaping behavior.
    private static readonly bool[] UnescapedAscii = BuildUnescapedAscii();

    private static bool[] BuildUnescapedAscii()
    {
        bool[] table = new bool[128];
        for (int i = 0x20; i < 0x7F; i++)
            table[i] = !JavaScriptEncoder.Default.WillEncode(i);
        return table;
    }

    // A tight true upper bound on the serialized size of a JSON string literal: 1 byte per
    // untouched ASCII char, 6 per anything else (the \uXXXX worst case — every escape form and
    // every UTF-8/surrogate encoding is ≤ 6 bytes per UTF-16 code unit), plus the quotes. For the
    // path-dominated strings in a report this sits within a few percent of the exact size, versus
    // the flat 6-bytes-per-char bound it replaces (which forced an exact-serialization fallback
    // near the budget — records were serialized twice, once to measure and once for the wire).
    internal static long StringUpperBound(string? value) =>
        value is null ? 0 : StringUpperBound(value.AsSpan());

    /// <summary>Span overload so a caller can bound a slice of a larger string — notably the file-name
    /// segment of an absolute path — without allocating the substring just to measure it.</summary>
    internal static long StringUpperBound(ReadOnlySpan<char> value)
    {
        long bytes = 2;   // the surrounding quotes
        foreach (char c in value)
            bytes += c < 128 && UnescapedAscii[c] ? 1 : 6;
        return bytes;
    }

    private async Task<FileEvaluation?> EvaluateFileAsync(
        Profile profile,
        Payload payload,
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot,
        bool hasTransformers,
        RunCounters counters,
        CancellationToken ct)
    {
        // The scanner captures the stat snapshot for free during enumeration; only fall back to a
        // dedicated stat when it didn't (e.g. a single-file scope payload).
        FileMetadata? metadata = payload.Metadata;
        if (metadata is null)
        {
            var metadataResult = FileMetadataReader.Read(payload.SourcePath);
            if (!metadataResult.TryGetValue(out metadata) || metadata is null)
            {
                // The file vanished or turned unreadable between enumeration and stat — a race,
                // not a reportable plan item.
                metadataResult.TryGetError(out string? statError);
                logger.LogWarning("Dry-run skipping {Path}: {Error}", payload.SourcePath,
                    statError ?? $"file not found: {payload.SourcePath}");
                return null;
            }
        }

        PhysicalFile sourceFile = new()
        {
            Path = payload.SourcePath,
            Root = payload.SourceRoot,
            Length = metadata!.Length,
            LastWritten = metadata.LastWritten,
            IsReparsePoint = metadata.IsSymlink,
        };

        string relativePath = Path.GetRelativePath(payload.SourceRoot, payload.SourcePath);

        if (filtersBySourceRoot.TryGetValue(payload.SourceRoot, out CompiledFilterSet? filters))
        {
            FilterInput input = FilterInput.For(payload.SourcePath, relativePath, metadata, filters.HasPatternRules);
            FilterDecision decision = filters.Evaluate(in input);
            if (!decision.Matched)
            {
                return new FileEvaluation(
                    sourceFile,
                    new VirtualFileOperation
                    {
                        Path = payload.SourcePath,
                        Root = payload.SourceRoot,
                        Kind = OperationKind.SkippedByFilter,
                        Detail = decision.DecidingRule,
                    },
                    [],
                    []);
            }
        }

        // Same resolution the live plan builder uses — shared so a dry run cannot drift from the run
        // it is predicting.
        TargetPathLayout layout = TargetPathLayout.For(profile, payload.SourcePath, relativePath);

        List<PhysicalFile> destinationFiles = [];
        List<VirtualFileOperation> destinationOps = [];
        byte[]? cachedSourceHash = null;

        foreach (TargetConfig target in profile.Targets)
        {
            if (ct.IsCancellationRequested)
                break;      // SimulateAsync's cancellation catch turns this into Canceled
            string prospective = layout.Resolve(target.Path);

            if (hasTransformers)
            {
                // §4.10: the transformed output does not exist to hash or compare, so every target
                // outcome for a transformer profile is Unknown. (Reachable only via hand-edited JSON
                // in this slice — the UI never emits transformers.)
                destinationOps.Add(new VirtualFileOperation
                {
                    Path = prospective,
                    Root = target.Path,
                    Kind = OperationKind.Unknown,
                    SourceIndex = 0,   // content from this source; remapped to the global index on append
                    SubjectIndex = -1,
                    Detail = "requires transform",
                });
                continue;
            }

            TargetEvaluation te = await EvaluateTargetAsync(
                profile.Policies, payload.SourcePath, metadata, prospective, target.Path, cachedSourceHash, counters, ct)
                .ConfigureAwait(false);
            cachedSourceHash = te.SourceHash;

            // Splice this target's pre-existing file (if any) into the bundle's destination-file list
            // and rewrite the ops that reference it (SubjectIndex == 0) to that bundle-local position.
            int localSubjectIndex = destinationFiles.Count;
            bool hasExisting = te.ExistingFile is not null;
            if (hasExisting)
                destinationFiles.Add(te.ExistingFile!);
            foreach (VirtualFileOperation op in te.Ops)
                destinationOps.Add(op with
                {
                    SubjectIndex = op.SubjectIndex == 0 && hasExisting ? localSubjectIndex : -1,
                });
        }

        // The file processes unless every target is an unchanged skip (an all-unchanged job closes
        // Skipped without committing, §3.4.1, so no source disposition runs). Only SkipUnchanged
        // counts as unchanged; SkipConflict, renames and writes are all "processing".
        bool allUnchanged = destinationOps.Count > 0
            && destinationOps.All(o => o.Kind == OperationKind.SkipUnchanged);

        VirtualFileOperation sourceOp = new()
        {
            Path = payload.SourcePath,
            Root = payload.SourceRoot,
            Kind = allUnchanged ? OperationKind.SkippedUnchanged : OperationKind.Processed,
            SourceDisposition = allUnchanged ? null : profile.Policies.OnSuccess,
        };

        return new FileEvaluation(sourceFile, sourceOp, destinationFiles, destinationOps);
    }

    /// <summary>One target's evaluation: the operation(s) it produces, the pre-existing destination
    /// file it touches (if any), and the (possibly newly computed) cached source hash. Within
    /// <see cref="Ops"/>, <c>SourceIndex == 0</c> means "the source file", and <c>SubjectIndex == 0</c>
    /// means "<see cref="ExistingFile"/>"; the caller remaps both to bundle-local positions.</summary>
    private readonly record struct TargetEvaluation(
        IReadOnlyList<VirtualFileOperation> Ops, PhysicalFile? ExistingFile, byte[]? SourceHash);

    /// <summary>Per-target simulation, in the executor's order: unchanged-check FIRST
    /// (spec §3.4.1, before conflict resolution), then the read-only conflict probe.</summary>
    private async Task<TargetEvaluation> EvaluateTargetAsync(
        PolicySettings policies,
        string sourcePath,
        FileMetadata metadata,
        string prospectivePath,
        string targetRoot,
        byte[]? cachedSourceHash,
        RunCounters counters,
        CancellationToken ct)
    {
        // One stat serves as both the existence probe and the metadata read: a null value means
        // the target does not exist; a failed read means it exists (or is unknowable) but is
        // unreadable — kept on the conflict-probe path so the preview stays Overwrite, never a
        // silently reclassified New.
        counters.CountExistenceProbe();
        var existing = FileMetadataReader.Read(prospectivePath);
        existing.TryGetValue(out FileMetadata? existingMeta);
        bool finalExists = existingMeta is not null || existing.IsFailure;
        PhysicalFile? existingFile = existingMeta is null ? null : new PhysicalFile
        {
            Path = prospectivePath,
            Root = targetRoot,
            Length = existingMeta.Length,
            LastWritten = existingMeta.LastWritten,
            IsReparsePoint = existingMeta.IsSymlink,
        };
        int subject = existingFile is not null ? 0 : -1;

        VirtualFileOperation Op(OperationKind kind, string path, int sourceIndex, int subjectIndex, string? detail) => new()
        {
            Path = path,
            Root = targetRoot,
            Kind = kind,
            SourceIndex = sourceIndex,
            SubjectIndex = subjectIndex,
            Detail = detail,
        };

        TargetEvaluation Single(VirtualFileOperation op, byte[]? hash) =>
            new([op], existingFile, hash);

        if (existingMeta is not null && existingMeta.Length == metadata.Length)
        {
            if (policies.VerificationMethod is VerificationMethod.Sha256 or VerificationMethod.XxHash128)
            {
                Result<byte[], JobError> targetHash;
                if (cachedSourceHash is null)
                {
                    // First hashing target for this file: the source and target full-file reads are
                    // independent, so run them concurrently — max(src, tgt) instead of src + tgt.
                    // HashFileToBytesAsync never throws (cancellation and errors come back as Result
                    // states), so WhenAll cannot fault and neither task is abandoned.
                    counters.CountHash(metadata.Length);
                    counters.CountHash(existingMeta.Length);
                    Task<Result<byte[], JobError>> sourceTask =
                        hasher.HashFileToBytesAsync(sourcePath, policies.VerificationMethod, ct);
                    Task<Result<byte[], JobError>> targetTask =
                        hasher.HashFileToBytesAsync(prospectivePath, policies.VerificationMethod, ct);
                    await Task.WhenAll(sourceTask, targetTask).ConfigureAwait(false);

                    Result<byte[], JobError> sourceHash = sourceTask.Result;
                    targetHash = targetTask.Result;
                    if (sourceHash.IsCanceled)
                        // Placeholder — discarded by SimulateAsync's cancellation catch.
                        return Single(Op(OperationKind.Unknown, prospectivePath, 0, subject, "canceled"), cachedSourceHash);
                    if (sourceHash.TryGetError(out JobError? hashError))
                        return Single(Op(OperationKind.Unknown, prospectivePath, 0, subject,
                            $"could not hash the source: {hashError.Message}"), null);
                    sourceHash.TryGetValue(out cachedSourceHash);
                }
                else
                {
                    counters.CountHash(existingMeta.Length);
                    targetHash = await hasher.HashFileToBytesAsync(prospectivePath, policies.VerificationMethod, ct).ConfigureAwait(false);
                }

                if (targetHash.IsCanceled)
                    return Single(Op(OperationKind.Unknown, prospectivePath, 0, subject, "canceled"), cachedSourceHash);
                if (targetHash.TryGetError(out JobError? targetHashError))
                    return Single(Op(OperationKind.Unknown, prospectivePath, 0, subject,
                        $"could not hash the existing target: {targetHashError.Message}"), cachedSourceHash);
                targetHash.TryGetValue(out byte[]? existingHash);

                if (cachedSourceHash is not null && existingHash is not null
                    && existingHash.AsSpan().SequenceEqual(cachedSourceHash))
                    return Single(Op(OperationKind.SkipUnchanged, prospectivePath, 0, subject,
                        $"identical content ({policies.VerificationMethod})"), cachedSourceHash);
            }
            else if (existingMeta.LastWritten == metadata.LastWritten)
            {
                // VerificationMethod.None: best-effort size + mtime equality (spec §3.4.1).
                return Single(Op(OperationKind.SkipUnchanged, prospectivePath, 0, subject,
                    "same size and modified time (best-effort — VerificationMethod is None)"), cachedSourceHash);
            }
        }

        var probe = conflictResolver.Probe(prospectivePath, policies.ConflictResolution, metadata.LastWritten,
            finalExists, existingMeta?.LastWritten ?? default);
        if (probe.TryGetError(out JobError? probeError))
            return Single(Op(OperationKind.Unknown, prospectivePath, 0, subject, probeError.Message), cachedSourceHash);
        probe.TryGetValue(out ConflictOutcome? outcome);

        if (outcome!.Action == ConflictAction.SkipExistingKept)
            return Single(Op(OperationKind.SkipConflict, prospectivePath, 0, subject,
                $"existing file kept ({policies.ConflictResolution})"), cachedSourceHash);

        if (!string.Equals(outcome.FinalPath, prospectivePath, StringComparison.OrdinalIgnoreCase))
        {
            // A rename routes the incoming file to a suffixed path AND leaves the pre-existing file in
            // place — emitted as two destination operations. The Rename is a target of this source; the
            // kept original is destination-only (SourceIndex == -1) so it never shows as a source target.
            VirtualFileOperation renameOp = Op(OperationKind.Rename, outcome.FinalPath, 0, -1,
                "renamed to avoid a conflict with the existing file");
            VirtualFileOperation keptOp = Op(OperationKind.Untouched, prospectivePath, -1, subject,
                "kept (an incoming file was renamed around it)");
            return new TargetEvaluation([renameOp, keptOp], existingFile, cachedSourceHash);
        }

        if (finalExists)
        {
            string? detail = existingMeta is not null
                ? $"existing file last modified {existingMeta.LastWritten:yyyy-MM-dd HH:mm:ss} UTC"
                : "would overwrite the existing file";
            return Single(Op(OperationKind.Overwrite, prospectivePath, 0, subject, detail), cachedSourceHash);
        }

        return Single(Op(OperationKind.New, prospectivePath, 0, -1, null), cachedSourceHash);
    }
}
