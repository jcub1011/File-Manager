using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
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
/// by integer index. Index assignment is deterministic (candidates are sorted by source path, then
/// bundles are appended in that order), so a truncated report is a valid prefix and the streamed and
/// batched reports agree.</summary>
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
    /// (<see cref="UpperBoundBytes(PhysicalFile)"/> / <see cref="StringUpperBound"/>, never actual
    /// serialization) and the report truncates when the running total would exceed it (16 MiB minus
    /// the response envelope and headroom for future additive fields). Because the estimate is an
    /// upper bound, the true serialized report is always smaller than the budget. The file-count cap
    /// is a secondary bound on UI row-building work.</summary>
    internal const int MaxReportBytes = 12 * 1024 * 1024;
    internal const int MaxReportedFiles = 50_000;

    /// <summary>Streaming (<see cref="SimulateStreamAsync"/>) has no report ceiling — the report is
    /// split across many frames, so a chunk only needs to sit comfortably under the 16 MiB frame
    /// cap. A traditional ~1 MiB per chunk (bounded by the cheap <see cref="UpperBoundBytes(PhysicalFile)"/>
    /// estimate, so the true serialized size is always smaller) keeps per-message memory low and
    /// leaves generous headroom under the cap. On a local pipe the extra frames cost nothing.</summary>
    internal const int ChunkByteThreshold = 1 * 1024 * 1024;

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

    /// <summary>Test seam: production uses <see cref="ChunkByteThreshold"/>; tests shrink it so a
    /// stream splits into several chunks without generating a megabyte of files.</summary>
    internal int ChunkByteBudget { get; init; } = ChunkByteThreshold;

    /// <summary>Test seam: production uses <see cref="MaxStreamedFiles"/>; tests shrink it so the
    /// candidate cap is reachable without generating half a million files.</summary>
    internal int MaxScannedCandidates { get; init; } = MaxStreamedFiles;

    /// <summary>Test seam: production uses <see cref="MaxReportedFiles"/>; tests shrink it so the
    /// batched file cap is reachable without generating tens of thousands of files.</summary>
    internal int MaxBatchCandidates { get; init; } = MaxReportedFiles;

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
        PipelineOutcome outcome;
        try
        {
            outcome = await ScanAndEvaluateAsync(
                profile, scopePath, filtersBySourceRoot!, hasTransformers, MaxBatchCandidates, counters,
                progress: null, ct)
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
        List<FileEvaluation> evaluations = outcome.Evaluations;
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
        bool scanDestinations = profile.EffectiveScanDestination;
        DestinationSweepResult sweep = scanDestinations
            ? destinationProjector.Sweep(profile, builder.Survivors, truncated, ct)
            : new DestinationSweepResult([], []);
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
            "{Probes} existence probes, {Stats} existing-target stats, {HashCount} files hashed / {HashBytes:N0} bytes)",
            profile.Id, builder.SourceFiles.Count, builder.DestinationFiles.Count,
            (completedAt - startedAt).TotalMilliseconds, truncated ? " (report truncated)" : "",
            outcome.ScanMs, outcome.EvalMs,
            counters.ExistenceProbes, counters.ExistingStats, counters.FilesHashed, counters.BytesHashed);

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
    /// the single-frame budget reports every file. Reuses the same scan → sort → batched read-only
    /// evaluation; results are buffered into a chunk and flushed once its cheap upper-bound size
    /// crosses <see cref="ChunkByteThreshold"/>. Indices are assigned globally across chunks so the
    /// consumer can concatenate them. The destination sweep is left to the handler (which runs it
    /// after the file phase). A fatal setup/scan error is a single failure item that ends the stream;
    /// cancellation surfaces as <see cref="OperationCanceledException"/> from the enumerator.</summary>
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
        // buffer while a worker pool evaluates them as they arrive. Chunks cannot be yielded before
        // the pipeline completes — output order is fixed by the sort below, so the first chunk needs
        // the full evaluated set regardless; the win is the scan/eval overlap, not first-chunk
        // latency. A fatal fault ends the stream with a failure; cancellation propagates as an
        // OperationCanceledException from the enumerator, exactly as before.
        RunCounters counters = new();
        PipelineOutcome outcome = await ScanAndEvaluateAsync(
            profile, scopePath, filtersBySourceRoot!, hasTransformers, MaxScannedCandidates, counters,
            progress, ct)
            .ConfigureAwait(false);

        if (outcome.FatalScanError is not null)
        {
            logger.LogError("Dry-run (stream) for profile {ProfileId} failed: scan error: {Message}",
                profile.Id, outcome.FatalScanError);
            yield return $"scan failed: {outcome.FatalScanError}";
            yield break;
        }

        bool scanTruncated = outcome.ScanTruncated;
        if (scanTruncated)
            logger.LogWarning(
                "Dry-run (stream) for profile {ProfileId} hit the {Cap:N0}-candidate safety bound; " +
                "report truncated — the scan found more",
                profile.Id, MaxScannedCandidates);

        // Fix the output order (source path) and flush a chunk whenever the buffer's upper-bound
        // size crosses the threshold. Indices are global across chunks (tracked by the accumulator)
        // so the client simply concatenates.
        List<FileEvaluation> evaluations = outcome.Evaluations;
        evaluations.Sort(static (a, b) =>
            string.Compare(a.SourceFile.Path, b.SourceFile.Path, StringComparison.OrdinalIgnoreCase));

        StreamAccumulator accumulator = new();
        bool anyEmitted = false;
        foreach (FileEvaluation evaluation in evaluations)
        {
            accumulator.Add(evaluation);
            if (accumulator.Bytes >= ChunkByteBudget)
            {
                yield return Result<DryRunChunk, string>.Success(accumulator.Flush() with { ScanTruncated = scanTruncated });
                anyEmitted = true;
            }
        }
        if (accumulator.HasData)
        {
            yield return Result<DryRunChunk, string>.Success(accumulator.Flush() with { ScanTruncated = scanTruncated });
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
                "{Stats} existing-target stats, {HashCount} files hashed / {HashBytes:N0} bytes)",
                profile.Id, accumulator.TotalSourceFiles, (completedAt - startedAt).TotalMilliseconds,
                outcome.ScanMs, outcome.EvalMs,
                counters.ExistenceProbes, counters.ExistingStats, counters.FilesHashed, counters.BytesHashed);
    }

    /// <summary>The fused scan+evaluation pipeline's result. <see cref="Evaluations"/> is UNSORTED
    /// (completion order — the parallel scan's emission order is already non-deterministic); the
    /// caller sorts by source path before assembly, which is where index assignment happens. A
    /// fatal scan fault is carried as a value in <see cref="FatalScanError"/>, never thrown.
    /// <see cref="ScanMs"/> is the pump's span within the <see cref="EvalMs"/> pipeline wall time —
    /// the two overlap by design.</summary>
    private sealed record PipelineOutcome(
        List<FileEvaluation> Evaluations,
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
            finally
            {
                // Unconditional, so consumers never wait on a writer that is already gone. Leaving
                // the foreach on any path disposes the scan iterator, which runs the scanner's own
                // teardown (cancel producers → drain → dispose).
                channel.Writer.TryComplete();
                scanWatch.Stop();
            }
        });

        ConcurrentQueue<FileEvaluation> results = new();
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
                        results.Enqueue(evaluation);
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
            return new PipelineOutcome([], false, fatalError, scanWatch.ElapsedMilliseconds, evalWatch.ElapsedMilliseconds);
        if (consumerError is not null)
            ExceptionDispatchInfo.Capture(consumerError).Throw();
        ct.ThrowIfCancellationRequested();

        return new PipelineOutcome(
            [.. results], scanTruncated, null, scanWatch.ElapsedMilliseconds, evalWatch.ElapsedMilliseconds);
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

    /// <summary>One source file's contribution to the report: the source file node, its source-side
    /// operation (Processed / Skipped + disposition), and the destination files/operations its targets
    /// produce. Indices are bundle-local: a destination op's <c>SourceIndex == 0</c> means "this
    /// bundle's source file" (else <c>-1</c>), and its <c>SubjectIndex</c> is a 0-based position into
    /// this bundle's <see cref="DestinationFiles"/> (else <c>-1</c>). The report builders remap these
    /// to global positions on append.</summary>
    private sealed record FileEvaluation(
        PhysicalFile SourceFile,
        VirtualFileOperation SourceOp,
        IReadOnlyList<PhysicalFile> DestinationFiles,
        IReadOnlyList<VirtualFileOperation> DestinationOps);

    /// <summary>Per-run I/O accounting for the evaluation phase, surfaced in the completion log so a
    /// slow run attributes its time to a phase without a profiler. Incremented under
    /// <see cref="Parallel.ForEachAsync"/>, so all writes are interlocked.</summary>
    private sealed class RunCounters
    {
        private long _existenceProbes;
        private long _existingStats;
        private long _filesHashed;
        private long _bytesHashed;

        public long ExistenceProbes => Interlocked.Read(ref _existenceProbes);
        public long ExistingStats => Interlocked.Read(ref _existingStats);
        public long FilesHashed => Interlocked.Read(ref _filesHashed);
        public long BytesHashed => Interlocked.Read(ref _bytesHashed);

        public void CountExistenceProbe() => Interlocked.Increment(ref _existenceProbes);
        public void CountExistingStat() => Interlocked.Increment(ref _existingStats);

        public void CountHash(long bytes)
        {
            Interlocked.Increment(ref _filesHashed);
            Interlocked.Add(ref _bytesHashed, bytes);
        }
    }

    /// <summary>Appends a bundle's records into the four report lists, remapping bundle-local indices
    /// to the global positions given by <paramref name="sourceIndex"/> (this bundle's source-file
    /// position) and <paramref name="destBase"/> (the count of destination files already present).</summary>
    private static void AppendGlobalized(
        FileEvaluation bundle, int sourceIndex, int destBase,
        List<PhysicalFile> sourceFiles, List<PhysicalFile> destinationFiles,
        List<VirtualFileOperation> sourceOps, List<VirtualFileOperation> destinationOps)
    {
        sourceFiles.Add(bundle.SourceFile);
        sourceOps.Add(bundle.SourceOp with { SourceIndex = sourceIndex });
        foreach (PhysicalFile file in bundle.DestinationFiles)
            destinationFiles.Add(file);
        foreach (VirtualFileOperation op in bundle.DestinationOps)
            destinationOps.Add(op with
            {
                SourceIndex = op.SourceIndex == 0 ? sourceIndex : -1,
                SubjectIndex = op.SubjectIndex >= 0 ? destBase + op.SubjectIndex : -1,
            });
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
        /// positions exactly as <see cref="AppendGlobalized"/> does on the streamed path. Returns
        /// false (and sets <see cref="Truncated"/>) without keeping anything if it would cross the
        /// budget.</summary>
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

    /// <summary>Streaming accumulator: buffers one chunk's records while tracking global source/dest
    /// counts across all chunks, so operation indices stay valid positions into the fully assembled
    /// report. Sizing uses the cheap upper-bound estimate only (a chunk just needs to sit under the
    /// frame cap).</summary>
    private sealed class StreamAccumulator
    {
        private List<PhysicalFile> _sourceFiles = [];
        private List<PhysicalFile> _destinationFiles = [];
        private List<VirtualFileOperation> _sourceOps = [];
        private List<VirtualFileOperation> _destinationOps = [];
        private int _globalSourceCount;
        private int _globalDestCount;

        public long Bytes { get; private set; }
        public int TotalSourceFiles => _globalSourceCount;
        public bool HasData => _sourceFiles.Count > 0 || _destinationFiles.Count > 0;

        public void Add(FileEvaluation bundle)
        {
            AppendGlobalized(bundle, _globalSourceCount, _globalDestCount,
                _sourceFiles, _destinationFiles, _sourceOps, _destinationOps);
            _globalSourceCount += 1;
            _globalDestCount += bundle.DestinationFiles.Count;
            Bytes += BundleUpperBound(bundle);
        }

        public DryRunChunk Flush()
        {
            DryRunChunk chunk = new(_sourceFiles, _destinationFiles, _sourceOps, _destinationOps);
            _sourceFiles = [];
            _destinationFiles = [];
            _sourceOps = [];
            _destinationOps = [];
            Bytes = 0;
            return chunk;
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

    private static long BundleUpperBound(FileEvaluation bundle)
    {
        long bytes = UpperBoundBytes(bundle.SourceFile) + UpperBoundBytes(bundle.SourceOp);
        foreach (PhysicalFile f in bundle.DestinationFiles) bytes += UpperBoundBytes(f);
        foreach (VirtualFileOperation o in bundle.DestinationOps) bytes += UpperBoundBytes(o);
        return bytes;
    }

    private static long UpperBoundBytes(PhysicalFile file) =>
        PhysicalFileStructuralBytes + StringUpperBound(file.Path) + StringUpperBound(file.Root);

    private static long UpperBoundBytes(VirtualFileOperation op) =>
        OperationStructuralBytes + StringUpperBound(op.Path) + StringUpperBound(op.Root) + StringUpperBound(op.Detail);

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
    internal static long StringUpperBound(string? value)
    {
        if (value is null)
            return 0;
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
        int depth = SeparatorCount(relativePath);

        if (filtersBySourceRoot.TryGetValue(payload.SourceRoot, out CompiledFilterSet? filters))
        {
            // Normalize once here rather than per pattern rule inside the filter set — but only when
            // a pattern rule would actually consult it (the attribute filter, always present, does
            // not), so the common no-glob profile allocates no normalized string.
            string? normalized = filters.HasPatternRules ? NormalizeSeparators(relativePath) : null;
            FilterInput input = new(payload.SourcePath, relativePath, depth, metadata, normalized);
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

        // M:1 topologies force Flatten (spec §3.1.2); otherwise the profile's TargetLayout rules.
        bool flatten = profile.TargetLayout == TargetLayout.Flatten || profile.Sources.Count > 1;
        // Only the flatten branch uses the bare file name; PreserveStructure never allocates it.
        string? fileName = flatten ? Path.GetFileName(payload.SourcePath) : null;

        List<PhysicalFile> destinationFiles = [];
        List<VirtualFileOperation> destinationOps = [];
        byte[]? cachedSourceHash = null;

        foreach (TargetConfig target in profile.Targets)
        {
            if (ct.IsCancellationRequested)
                break;      // SimulateAsync's cancellation catch turns this into Canceled
            string prospective = flatten
                ? Path.Combine(target.Path, fileName!)
                : Path.Combine(target.Path, relativePath);

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

    private static int SeparatorCount(string value)
    {
        int count = 0;
        foreach (char c in value)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                count++;
        }
        return count;
    }

    private static string NormalizeSeparators(string relativePath) =>
        Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace('\\', '/');

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
