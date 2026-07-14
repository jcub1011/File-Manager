using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using FileManager.Contracts;
using FileManager.Contracts.Settings;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
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
    IProfileCatalog catalog,
    ISourceScanner scanner,
    IFilterCompiler filterCompiler,
    IFileHasher hasher,
    IConflictResolver conflictResolver,
    ISettingsProvider settings,
    TimeProvider time,
    DestinationProjector destinationProjector) : IDryRunEngine
{
    /// <summary>Report size guards: a serialized report must fit an IPC frame (16 MiB cap, §3.1).
    /// The byte budget is the guarantee — each record is measured as serialized and the report
    /// truncates when the running total would exceed it (16 MiB minus the response envelope and
    /// headroom for future additive fields). The file-count cap is a secondary bound on UI
    /// row-building work.</summary>
    internal const int MaxReportBytes = 12 * 1024 * 1024;
    internal const int MaxReportedFiles = 50_000;

    /// <summary>Streaming (<see cref="SimulateStreamAsync"/>) has no report ceiling — the report is
    /// split across many frames, so a chunk only needs to sit comfortably under the 16 MiB frame
    /// cap. A traditional ~1 MiB per chunk (bounded by the cheap <see cref="UpperBoundBytes(PhysicalFile)"/>
    /// estimate, so the true serialized size is always smaller) keeps per-message memory low and
    /// leaves generous headroom under the cap. On a local pipe the extra frames cost nothing.</summary>
    internal const int ChunkByteThreshold = 1 * 1024 * 1024;

    /// <summary>Streaming lifts the frame-cap ceiling, but not the good sense of an upper limit. The
    /// candidate buffer (<see cref="SimulateStreamAsync"/> phase 1) has to be fully materialized to
    /// sort by source path, so a pathological scan of millions of files would otherwise buffer — and
    /// then hash — all of them before the first chunk. This bound caps the buffered candidates
    /// (and therefore the evaluation work), the same way <see cref="MaxReportedFiles"/> bounds the
    /// batched path. Hitting it marks the report truncated.</summary>
    internal const int MaxStreamedFiles = 500_000;

    /// <summary>Candidates are evaluated in batches so the byte budget can halt evaluation early
    /// (see phase 2). Sized well above the resolved worker count so every worker stays busy, while
    /// capping the evaluation wasted past the truncation point to at most one batch.</summary>
    private const int EvaluationBatchSize = 512;

    /// <summary>Test seam: production uses <see cref="MaxReportBytes"/>; tests shrink it so
    /// truncation is reachable without tens of thousands of real files.</summary>
    internal int ReportByteBudget { get; init; } = MaxReportBytes;

    /// <summary>Test seam: production uses <see cref="ChunkByteThreshold"/>; tests shrink it so a
    /// stream splits into several chunks without generating a megabyte of files.</summary>
    internal int ChunkByteBudget { get; init; } = ChunkByteThreshold;

    /// <summary>Test seam: production uses <see cref="MaxStreamedFiles"/>; tests shrink it so the
    /// candidate cap is reachable without generating half a million files.</summary>
    internal int MaxScannedCandidates { get; init; } = MaxStreamedFiles;

    public async Task<Result<DryRunReport, string>> SimulateAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default)
    {
        DateTimeOffset startedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Dry-run started for profile {ProfileId} (scope {Scope})",
            profileId, scopePath ?? "<all sources>");

        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Dry-run requested for profile {ProfileId} which was not found", profileId);
            return $"profile {profileId} not found";
        }

        // One compiled set per source, keyed by the source root the scanner stamps on payloads.
        Result<Dictionary<string, CompiledFilterSet>, string> filtersResult = CompileFilters(profile);
        if (filtersResult.TryGetError(out string? compileError))
            return compileError;
        filtersResult.TryGetValue(out Dictionary<string, CompiledFilterSet>? filtersBySourceRoot);

        bool hasTransformers = profile.Transformers is { Count: > 0 };
        bool truncated = false;

        // Phase 1: drain the scan into a bounded candidate list. A fatal fault aborts the whole run;
        // warnings are logged and skipped. The file cap bounds both the buffered payloads and the
        // parallel evaluation work below. The scan honours the same Manual concurrency pin as the
        // evaluation and sweep (Automatic → the scanner auto-scales to the source medium).
        int? scanWorkers = DryRunConcurrency.ResolveManualWorkers(profile, settings.Current);
        Stopwatch scanWatch = Stopwatch.StartNew();
        List<Payload> candidates = [];
        try
        {
            foreach (var scanned in scanner.Scan(profile, TriggerKind.Cli, scopePath, scanWorkers, ct))
            {
                if (scanned.TryGetError(out EnumerationFault fault))
                {
                    if (fault.Severity == EnumerationSeverity.Fatal)
                    {
                        logger.LogError("Dry-run for profile {ProfileId} failed: scan error: {Message}",
                            profileId, fault.Message);
                        return $"scan failed: {fault.Message}";
                    }
                    logger.LogWarning("Dry-run enumeration warning: {Message}", fault.Message);
                    continue;
                }

                if (candidates.Count >= MaxReportedFiles)
                {
                    // Accepted with the parallel scan: emission order is non-deterministic, so a fatal
                    // walk-root fault produced after this cap is reached goes unobserved and the run
                    // resolves as truncated-success rather than failed. That is deliberate — the cap
                    // already means "we stopped looking", and the truncated flag communicates the
                    // incompleteness (see ISourceScanner.Scan remarks).
                    truncated = true;
                    break;
                }

                scanned.TryGetValue(out Payload? payload);
                candidates.Add(payload!);
            }
        }
        catch (OperationCanceledException)
        {
            // The parallel scan throws when the token trips (even before any payload); a cancelled
            // run resolves to Canceled, matching the evaluation phase below.
            return Result<DryRunReport, string>.Canceled();
        }

        // Phase 2: fix the output order up front by sorting candidates by source path, then evaluate
        // them in that order. A source file's index is its sorted position, so truncation keeps a
        // path-ordered *prefix* and no retained operation references a dropped file. Evaluating in
        // bounded batches keeps the concurrency of an all-at-once pass while capping the work wasted
        // past the truncation point to at most one batch. Every collaborator is read-only
        // (I-DRYRUN-RO) and stateless, so the parallel evaluation is race-free; positional writes into
        // the batch array make the append order deterministic regardless of completion order.
        scanWatch.Stop();
        candidates.Sort(static (a, b) =>
            string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));

        int maxConcurrency = ResolveWorkers(profile);
        RunCounters counters = new();
        Stopwatch evalWatch = Stopwatch.StartNew();
        using ReportBuilder builder = new(ReportByteBudget);
        try
        {
            for (int start = 0; start < candidates.Count && !builder.Truncated; start += EvaluationBatchSize)
            {
                int count = Math.Min(EvaluationBatchSize, candidates.Count - start);
                FileEvaluation?[] batch = new FileEvaluation?[count];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, count),
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                    async (j, token) =>
                    {
                        batch[j] = await EvaluateFileAsync(
                            profile, candidates[start + j], filtersBySourceRoot!, hasTransformers, counters, token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);

                foreach (FileEvaluation? evaluation in batch)
                {
                    if (evaluation is null)
                        continue;
                    if (!builder.TryAddBundle(evaluation))
                        break;      // budget full — Truncated is set, so the outer loop also stops
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A hash or target loop cut short by cancellation returns a partial/placeholder result;
            // cancellation turns the whole run into Canceled rather than a misleading partial report.
            return Result<DryRunReport, string>.Canceled();
        }
        evalWatch.Stop();

        truncated |= builder.Truncated;

        if (truncated)
            logger.LogWarning(
                "Dry-run report for profile {ProfileId} truncated at {FileCount} source files / ~{Bytes:N0} bytes " +
                "(caps: {ByteCap:N0} bytes, {FileCap} files) — the scan found more",
                profileId, builder.SourceFiles.Count, builder.ReportBytes, ReportByteBudget, MaxReportedFiles);

        // Phase 3: destination-only entries (pre-existing Untouched + Mirror orphans). Suppressed
        // entirely when the source pass truncated (the survivor set would be incomplete, so any
        // orphan classification is untrustworthy). Appended only while their size keeps the report
        // under budget; an overflow drops the rest and marks the report truncated.
        DestinationSweepResult sweep = destinationProjector.Project(
            profile, builder.DestinationOperations, truncated,
            DryRunConcurrency.ResolveManualWorkers(profile, settings.Current), ct);
        for (int i = 0; i < sweep.Files.Count; i++)
        {
            if (!builder.TryAddSweepEntry(sweep.Files[i], sweep.Ops[i]))
            {
                truncated = true;
                logger.LogWarning(
                    "Dry-run report for profile {ProfileId} truncated its destination entries " +
                    "(byte cap {ByteCap:N0}) — more pre-existing/orphan files exist",
                    profileId, ReportByteBudget);
                break;
            }
        }

        DateTimeOffset completedAt = time.GetUtcNow();
        logger.LogInformation(
            "Dry-run completed for profile {ProfileId}: {SourceCount} source files, {DestCount} destination files " +
            "in {ElapsedMs}ms{Truncated} (scan {ScanMs}ms, evaluation {EvalMs}ms; {Probes} existence probes, " +
            "{Stats} existing-target stats, {HashCount} files hashed / {HashBytes:N0} bytes)",
            profileId, builder.SourceFiles.Count, builder.DestinationFiles.Count,
            (completedAt - startedAt).TotalMilliseconds, truncated ? " (report truncated)" : "",
            scanWatch.ElapsedMilliseconds, evalWatch.ElapsedMilliseconds,
            counters.ExistenceProbes, counters.ExistingStats, counters.FilesHashed, counters.BytesHashed);

        return new DryRunReport
        {
            ProfileId = profileId,
            GeneratedAt = completedAt,
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
    public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
        Guid profileId, string? scopePath, [EnumeratorCancellation] CancellationToken ct = default)
    {
        DateTimeOffset startedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Dry-run (stream) started for profile {ProfileId} (scope {Scope})",
                profileId, scopePath ?? "<all sources>");

        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Dry-run requested for profile {ProfileId} which was not found", profileId);
            yield return $"profile {profileId} not found";
            yield break;
        }

        Result<Dictionary<string, CompiledFilterSet>, string> filtersResult = CompileFilters(profile);
        if (filtersResult.TryGetError(out string? compileError))
        {
            yield return compileError;
            yield break;
        }
        filtersResult.TryGetValue(out Dictionary<string, CompiledFilterSet>? filtersBySourceRoot);

        bool hasTransformers = profile.Transformers is { Count: > 0 };

        // Phase 1: drain the scan into a candidate list bounded by MaxScannedCandidates. A fatal fault
        // ends the stream with a failure; warnings are logged and skipped. The scan honours the same
        // Manual concurrency pin as the evaluation and sweep (Automatic → auto-scale to the medium).
        int? scanWorkers = DryRunConcurrency.ResolveManualWorkers(profile, settings.Current);
        Stopwatch scanWatch = Stopwatch.StartNew();
        List<Payload> candidates = [];
        bool scanTruncated = false;
        foreach (var scanned in scanner.Scan(profile, TriggerKind.Cli, scopePath, scanWorkers, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned.TryGetError(out EnumerationFault fault))
            {
                if (fault.Severity == EnumerationSeverity.Fatal)
                {
                    logger.LogError("Dry-run (stream) for profile {ProfileId} failed: scan error: {Message}",
                        profileId, fault.Message);
                    yield return $"scan failed: {fault.Message}";
                    yield break;
                }
                logger.LogWarning("Dry-run enumeration warning: {Message}", fault.Message);
                continue;
            }

            if (candidates.Count >= MaxScannedCandidates)
            {
                // As in the batched path: with the parallel scan a fatal walk-root fault produced after
                // this cap is reached goes unobserved and the stream truncates rather than fails. The
                // truncated flag carries the incompleteness (see ISourceScanner.Scan remarks).
                logger.LogWarning(
                    "Dry-run (stream) for profile {ProfileId} hit the {Cap:N0}-candidate safety bound; " +
                    "report truncated — the scan found more",
                    profileId, MaxScannedCandidates);
                scanTruncated = true;
                break;
            }

            scanned.TryGetValue(out Payload? payload);
            candidates.Add(payload!);
        }

        scanWatch.Stop();

        // Phase 2: fix the output order (source path), evaluate in bounded batches, and flush a chunk
        // whenever the buffer's upper-bound size crosses the threshold. Indices are global across
        // chunks (tracked by the accumulator) so the client simply concatenates.
        candidates.Sort(static (a, b) =>
            string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));

        int maxConcurrency = ResolveWorkers(profile);
        RunCounters counters = new();
        Stopwatch evalWatch = Stopwatch.StartNew();
        StreamAccumulator accumulator = new();
        bool anyEmitted = false;
        for (int start = 0; start < candidates.Count; start += EvaluationBatchSize)
        {
            int count = Math.Min(EvaluationBatchSize, candidates.Count - start);
            FileEvaluation?[] batch = new FileEvaluation?[count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                async (j, token) =>
                {
                    batch[j] = await EvaluateFileAsync(
                        profile, candidates[start + j], filtersBySourceRoot!, hasTransformers, counters, token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);

            foreach (FileEvaluation? evaluation in batch)
            {
                if (evaluation is null)
                    continue;
                accumulator.Add(evaluation);
                if (accumulator.Bytes >= ChunkByteBudget)
                {
                    yield return Result<DryRunChunk, string>.Success(accumulator.Flush() with { ScanTruncated = scanTruncated });
                    anyEmitted = true;
                }
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

        evalWatch.Stop();
        DateTimeOffset completedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Dry-run (stream) completed for profile {ProfileId}: {SourceCount} source files in {ElapsedMs}ms " +
                "(scan {ScanMs}ms, evaluation {EvalMs}ms; {Probes} existence probes, {Stats} existing-target stats, " +
                "{HashCount} files hashed / {HashBytes:N0} bytes)",
                profileId, accumulator.TotalSourceFiles, (completedAt - startedAt).TotalMilliseconds,
                scanWatch.ElapsedMilliseconds, evalWatch.ElapsedMilliseconds,
                counters.ExistenceProbes, counters.ExistingStats, counters.FilesHashed, counters.BytesHashed);
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

    /// <summary>Resolves the evaluation worker count for this run. The profile's own mode wins;
    /// <see cref="ConcurrencyMode.Inherit"/> defers to the global setting. Every path is clamped to
    /// at least 1 so <see cref="ParallelOptions.MaxDegreeOfParallelism"/> is always valid.</summary>
    internal int ResolveWorkers(Profile profile)
    {
        ConcurrencyOverride c = profile.Concurrency;
        return c.Mode switch
        {
            ConcurrencyMode.Manual => Math.Max(1, c.ManualWorkers ?? AutoWorkers()),
            ConcurrencyMode.Automatic => AutoWorkers(),
            _ => ResolveGlobalWorkers(),   // Inherit
        };
    }

    private int ResolveGlobalWorkers()
    {
        GlobalSettings global = settings.Current;
        return global.DryRunConcurrencyMode == ConcurrencyMode.Manual
            ? Math.Max(1, global.DryRunManualWorkers ?? AutoWorkers())
            : AutoWorkers();
    }

    // Evaluation-phase default worker count; see DryRunConcurrency.AutoWorkers for the rationale.
    private static int AutoWorkers() => DryRunConcurrency.AutoWorkers();

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
    /// in final (source-path) order. Serialization is not free, so it is skipped while a conservative
    /// upper-bound estimate proves the report still fits — the common case. Only once the upper bound
    /// could cross the budget does it fall back to exact measurement (re-establishing the running total
    /// from what's already kept). A bundle is added atomically, so a retained operation never
    /// references a dropped file.</summary>
    private sealed class ReportBuilder(long budget) : IDisposable
    {
        public List<PhysicalFile> SourceFiles { get; } = [];
        public List<PhysicalFile> DestinationFiles { get; } = [];
        public List<VirtualFileOperation> SourceOperations { get; } = [];
        public List<VirtualFileOperation> DestinationOperations { get; } = [];

        /// <summary>True once a unit was rejected because keeping it would cross the budget.</summary>
        public bool Truncated { get; private set; }

        /// <summary>Exact once measuring, otherwise the running upper-bound estimate.</summary>
        public long ReportBytes => _total;

        private long _total;
        private bool _exact;
        private ArrayBufferWriter<byte>? _buffer;
        private Utf8JsonWriter? _writer;

        /// <summary>Adds a whole per-file bundle atomically. Returns false (and sets
        /// <see cref="Truncated"/>) without keeping it if it would cross the budget.</summary>
        public bool TryAddBundle(FileEvaluation bundle)
        {
            if (!TryReserve(BundleUpperBound(bundle), () => BundleExactBytes(bundle)))
                return false;
            AppendGlobalized(bundle, SourceFiles.Count, DestinationFiles.Count,
                SourceFiles, DestinationFiles, SourceOperations, DestinationOperations);
            return true;
        }

        /// <summary>Adds one swept (file, op) pair atomically, wiring the op's SubjectIndex to the
        /// file's new position.</summary>
        public bool TryAddSweepEntry(PhysicalFile file, VirtualFileOperation op)
        {
            if (!TryReserve(UpperBoundBytes(file) + UpperBoundBytes(op),
                    () => ExactBytes(file) + ExactBytes(op)))
                return false;
            int subjectIndex = DestinationFiles.Count;
            DestinationFiles.Add(file);
            DestinationOperations.Add(op with { SubjectIndex = subjectIndex });
            return true;
        }

        private bool TryReserve(long upperBound, Func<long> exactBytes)
        {
            if (!_exact)
            {
                if (_total + upperBound <= budget)
                {
                    _total += upperBound;
                    return true;
                }
                // The upper bound could exceed the budget — switch to exact measurement and
                // re-establish the running total as the exact size of what's already kept.
                _exact = true;
                _buffer = new ArrayBufferWriter<byte>();
                _writer = new Utf8JsonWriter(_buffer);
                _total = ExactTotalOfKept();
            }

            long exact = exactBytes();
            if (_total + exact > budget)
            {
                Truncated = true;
                return false;
            }
            _total += exact;
            return true;
        }

        private long ExactTotalOfKept()
        {
            long total = 0;
            foreach (PhysicalFile f in SourceFiles) total += ExactBytes(f);
            foreach (PhysicalFile f in DestinationFiles) total += ExactBytes(f);
            foreach (VirtualFileOperation o in SourceOperations) total += ExactBytes(o);
            foreach (VirtualFileOperation o in DestinationOperations) total += ExactBytes(o);
            return total;
        }

        private long BundleExactBytes(FileEvaluation bundle)
        {
            long total = ExactBytes(bundle.SourceFile) + ExactBytes(bundle.SourceOp);
            foreach (PhysicalFile f in bundle.DestinationFiles) total += ExactBytes(f);
            foreach (VirtualFileOperation o in bundle.DestinationOps) total += ExactBytes(o);
            return total;
        }

        private int ExactBytes(PhysicalFile file)
        {
            _buffer!.ResetWrittenCount();
            _writer!.Reset(_buffer);
            JsonSerializer.Serialize(_writer, file, FileManagerJsonContext.Default.PhysicalFile);
            return _buffer.WrittenCount;
        }

        private int ExactBytes(VirtualFileOperation op)
        {
            _buffer!.ResetWrittenCount();
            _writer!.Reset(_buffer);
            JsonSerializer.Serialize(_writer, op, FileManagerJsonContext.Default.VirtualFileOperation);
            return _buffer.WrittenCount;
        }

        public void Dispose() => _writer?.Dispose();
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
    // so it is a true upper bound alongside the 6-bytes-per-char string bound.
    private const int PhysicalFileStructuralBytes = 256;
    private const int OperationStructuralBytes = 320;

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

    // JSON's absolute worst case is 6 UTF-8 bytes per UTF-16 code unit (\uXXXX, incl. surrogates),
    // plus the surrounding quotes — a true upper bound regardless of escaping or non-ASCII content.
    private static long StringUpperBound(string? value) => value is null ? 0 : (long)value.Length * 6 + 2;

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
