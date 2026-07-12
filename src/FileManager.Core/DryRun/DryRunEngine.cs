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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>Spec §8: simulates a full run with ZERO filesystem mutation (I-DRYRUN-RO) — every
/// collaborator here is read-only (scan, stat, hash, existence probes). Reports matches with
/// deciding filters, unchanged skips, per-target write/overwrite/rename outcomes, and the
/// source disposition that would occur.</summary>
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
    /// The byte budget is the guarantee — each result is measured as serialized and the report
    /// truncates when the running total would exceed it (16 MiB minus the response envelope and
    /// headroom for future additive fields). The file-count cap is a secondary bound on UI
    /// row-building work. A paged/streamed report is a future additive Contracts change.</summary>
    internal const int MaxReportBytes = 12 * 1024 * 1024;
    internal const int MaxReportedFiles = 50_000;

    /// <summary>Streaming (<see cref="SimulateStreamAsync"/>) has no report ceiling — the report is
    /// split across many frames, so a chunk only needs to sit comfortably under the 16 MiB frame
    /// cap. A traditional ~1 MiB per chunk (bounded by the cheap <see cref="UpperBoundBytes"/>
    /// estimate, so the true serialized size is always smaller) keeps per-message memory low and
    /// leaves generous headroom under the cap. On a local pipe the extra frames cost nothing.</summary>
    internal const int ChunkByteThreshold = 1 * 1024 * 1024;

    /// <summary>Streaming lifts the frame-cap ceiling, but not the good sense of an upper limit. The
    /// candidate buffer (<see cref="SimulateStreamAsync"/> phase 1) has to be fully materialized to
    /// sort by source path, so a pathological scan of millions of files would otherwise buffer — and
    /// then hash — all of them before the first chunk. This bound caps the buffered candidates
    /// (and therefore the evaluation work), the same way <see cref="MaxReportedFiles"/> bounds the
    /// batched path; it is set far above the old ~50k cap. Hitting it marks the report truncated.</summary>
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

        // Phase 1: drain the scan into a bounded candidate list. Fault handling matches the old
        // inline loop — a fatal fault aborts the whole run; warnings are logged and skipped. The
        // file cap bounds both the buffered payloads and the parallel evaluation work below.
        List<Payload> candidates = [];
        foreach (var scanned in scanner.Scan(profile, TriggerKind.Cli, scopePath))
        {
            if (ct.IsCancellationRequested)
                return Result<DryRunReport, string>.Canceled();

            if (scanned.TryGetError(out EnumerationFault fault))
            {
                if (fault.Severity == EnumerationSeverity.Fatal)
                {
                    logger.LogError("Dry-run for profile {ProfileId} failed: scan error: {Message}",
                        profileId, fault.Message);
                    return $"scan failed: {fault.Message}";
                }
                // Warnings are logged, not reported — the frozen DryRunReport has no warnings
                // field; adding one later is an additive Contracts change.
                logger.LogWarning("Dry-run enumeration warning: {Message}", fault.Message);
                continue;
            }

            if (candidates.Count >= MaxReportedFiles)
            {
                truncated = true;
                break;
            }

            scanned.TryGetValue(out Payload? payload);
            candidates.Add(payload!);
        }

        // Phase 2: fix the output order up front by sorting candidates by source path, then evaluate
        // them in that order. Truncation keeps a path-ordered *prefix* (SourcePath == payload path,
        // and filtered candidates evaluate to null and never count), so once the byte budget fills,
        // every remaining candidate sorts after the last kept file and would be truncated anyway —
        // evaluating it (including hashing its contents) would be wasted I/O, so we stop. Evaluating
        // in bounded batches keeps the concurrency of an all-at-once pass while capping the work
        // wasted past the truncation point to at most one batch. Every collaborator is read-only
        // (I-DRYRUN-RO) and stateless, so the parallel evaluation is race-free; positional writes
        // into the batch array make the kept order deterministic regardless of completion order.
        candidates.Sort(static (a, b) =>
            string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));

        int maxConcurrency = ResolveWorkers(profile);
        using ByteBudget budget = new(ReportByteBudget);
        try
        {
            for (int start = 0; start < candidates.Count && !budget.Truncated; start += EvaluationBatchSize)
            {
                int count = Math.Min(EvaluationBatchSize, candidates.Count - start);
                DryRunFileResult?[] batch = new DryRunFileResult?[count];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, count),
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                    async (j, token) =>
                    {
                        batch[j] = await EvaluateFileAsync(
                            profile, candidates[start + j], filtersBySourceRoot!, hasTransformers, token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);

                foreach (DryRunFileResult? result in batch)
                {
                    if (result is null)
                        continue;
                    if (!budget.TryAdd(result))
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

        List<DryRunFileResult> files = budget.Kept;
        truncated |= budget.Truncated;
        long reportBytes = budget.ReportBytes;

        if (truncated)
            logger.LogWarning(
                "Dry-run report for profile {ProfileId} truncated at {FileCount} files / ~{Bytes:N0} bytes " +
                "(caps: {ByteCap:N0} bytes, {FileCap} files) — the scan found more",
                profileId, files.Count, reportBytes, ReportByteBudget, MaxReportedFiles);

        // Phase 3: destination-only entries (preexisting-Untouched + Mirror orphans). Suppressed
        // entirely when the source pass truncated (the survivor set would be incomplete, so any
        // orphan classification is untrustworthy). What survives must still fit the frame, so the
        // entries are appended only while their upper-bound size keeps the report under the budget;
        // an overflow drops the rest and marks the report truncated (same guarantee as Files).
        IReadOnlyList<DryRunDestinationEntry> allDestinations =
            destinationProjector.Project(profile, files, truncated, ct);
        List<DryRunDestinationEntry> destinations = [];
        foreach (DryRunDestinationEntry entry in allDestinations)
        {
            long size = DestinationUpperBoundBytes(entry);
            if (reportBytes + size > ReportByteBudget)
            {
                truncated = true;
                logger.LogWarning(
                    "Dry-run report for profile {ProfileId} truncated its destination entries at {Count} " +
                    "(byte cap {ByteCap:N0}) — more pre-existing/orphan files exist",
                    profileId, destinations.Count, ReportByteBudget);
                break;
            }
            reportBytes += size;
            destinations.Add(entry);
        }

        DateTimeOffset completedAt = time.GetUtcNow();
        logger.LogInformation(
            "Dry-run completed for profile {ProfileId}: {FileCount} files, {DestCount} destination entries " +
            "in {ElapsedMs}ms{Truncated}",
            profileId, files.Count, destinations.Count, (completedAt - startedAt).TotalMilliseconds,
            truncated ? " (report truncated)" : "");
        return new DryRunReport(profileId, completedAt, files, truncated, destinations);
    }

    /// <summary>Streaming counterpart to <see cref="SimulateAsync"/> (spec §8): yields the report as
    /// a sequence of source-path-ordered chunks instead of one bounded, truncatable report, so a
    /// run larger than the single-frame budget reports every file. Reuses the same scan → sort →
    /// batched read-only evaluation; only the accumulation differs — results are buffered and a
    /// chunk is emitted once its cheap upper-bound size crosses <see cref="ChunkByteThreshold"/>.
    /// A fatal setup/scan error is yielded as a single failure item and ends the stream; cancellation
    /// surfaces as <see cref="OperationCanceledException"/> from the enumerator (the async-stream
    /// convention), never a partial "success".</summary>
    public async IAsyncEnumerable<Result<IReadOnlyList<DryRunFileResult>, string>> SimulateStreamAsync(
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

        // Phase 1: drain the scan into a candidate list. The buffer must be fully materialized to
        // sort by source path (phase 2), so it is bounded by MaxScannedCandidates — a memory/CPU
        // backstop, far above the batched path's ceiling, that stops a pathological scan from
        // buffering and hashing millions of files. A fatal fault ends the stream with a failure;
        // warnings are logged and skipped (matching SimulateAsync). Cancellation throws, which the
        // consumer treats as a cancelled run.
        List<Payload> candidates = [];
        bool scanTruncated = false;
        foreach (var scanned in scanner.Scan(profile, TriggerKind.Cli, scopePath))
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

        // Phase 2: fix the output order (source path), evaluate in bounded batches, and flush a chunk
        // whenever the buffer's upper-bound size crosses the threshold. Same read-only, race-free
        // evaluation as SimulateAsync; positional writes keep the kept order deterministic.
        candidates.Sort(static (a, b) =>
            string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));

        int maxConcurrency = ResolveWorkers(profile);
        List<DryRunFileResult> chunk = [];
        long chunkBytes = 0;
        int totalKept = 0;
        for (int start = 0; start < candidates.Count; start += EvaluationBatchSize)
        {
            int count = Math.Min(EvaluationBatchSize, candidates.Count - start);
            DryRunFileResult?[] batch = new DryRunFileResult?[count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = ct },
                async (j, token) =>
                {
                    batch[j] = await EvaluateFileAsync(
                        profile, candidates[start + j], filtersBySourceRoot!, hasTransformers, token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);

            foreach (DryRunFileResult? result in batch)
            {
                if (result is null)
                    continue;
                chunk.Add(result);
                totalKept++;
                chunkBytes += UpperBoundBytes(result);
                if (chunkBytes >= ChunkByteBudget)
                {
                    yield return Result<IReadOnlyList<DryRunFileResult>, string>.Success(chunk);
                    chunk = [];
                    chunkBytes = 0;
                }
            }
        }
        if (chunk.Count > 0)
            yield return Result<IReadOnlyList<DryRunFileResult>, string>.Success(chunk);

        DateTimeOffset completedAt = time.GetUtcNow();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Dry-run (stream) completed for profile {ProfileId}: {FileCount} files in {ElapsedMs}ms{Truncated}",
                profileId, totalKept, (completedAt - startedAt).TotalMilliseconds,
                scanTruncated ? " (candidate cap reached)" : "");
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

    // Reserve one core for the system and keep an 8-worker ceiling — per-file evaluation is
    // I/O-bound (stat + existence probe + up to two SHA-256 hashes), so beyond ~8 concurrent
    // hashers the disk, not the CPU, is the bottleneck. Floor of 1 covers single-core machines.
    private static int AutoWorkers() => Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));

    /// <summary>Streaming truncation to the serialized byte budget, fed results in final
    /// (source-path) order as they are evaluated. Serialization is not free, so it is skipped
    /// entirely while a conservative upper-bound estimate proves the report still fits — the common
    /// case. Only once the upper bound could cross the budget does it fall back to exact measurement
    /// (re-establishing the running total from the already-kept results), preserving precise
    /// truncation near the cap. <see cref="TryAdd"/> returns false the moment the budget is crossed
    /// so the caller can stop evaluating further candidates instead of hashing files that cannot
    /// fit.</summary>
    private sealed class ByteBudget(long budget) : IDisposable
    {
        private readonly List<DryRunFileResult> _kept = [];
        private long _total;
        private bool _exactMode;
        private ArrayBufferWriter<byte>? _measureBuffer;
        private Utf8JsonWriter? _measureWriter;

        /// <summary>The kept results, in the order they were added.</summary>
        public List<DryRunFileResult> Kept => _kept;

        /// <summary>True once a result was rejected because keeping it would cross the budget.</summary>
        public bool Truncated { get; private set; }

        /// <summary>Exact once measuring, otherwise the running upper-bound estimate.</summary>
        public long ReportBytes => _total;

        /// <summary>Keeps <paramref name="result"/> if it fits; otherwise sets
        /// <see cref="Truncated"/> and returns false without keeping it.</summary>
        public bool TryAdd(DryRunFileResult result)
        {
            if (!_exactMode)
            {
                long upperBound = UpperBoundBytes(result);
                if (_total + upperBound <= budget)
                {
                    // Actual serialized size <= upper bound <= budget, so this is provably safe to
                    // keep without serializing anything.
                    _total += upperBound;
                    _kept.Add(result);
                    return true;
                }

                // The upper bound could exceed the budget — switch to exact measurement and
                // re-establish the running total as the exact size of what's already kept.
                _exactMode = true;
                _measureBuffer = new ArrayBufferWriter<byte>();
                _measureWriter = new Utf8JsonWriter(_measureBuffer);
                _total = 0;
                foreach (DryRunFileResult keptResult in _kept)
                    _total += ExactBytes(keptResult, _measureBuffer, _measureWriter);
            }

            int resultBytes = ExactBytes(result, _measureBuffer!, _measureWriter!);
            if (_total + resultBytes > budget)
            {
                Truncated = true;
                return false;
            }
            _total += resultBytes;
            _kept.Add(result);
            return true;
        }

        public void Dispose() => _measureWriter?.Dispose();
    }

    // Measured exactly (a standalone record serializes byte-identically to the same record as a
    // Files[] element) using the reused writer so measuring doesn't allocate a byte[] per result.
    private static int ExactBytes(DryRunFileResult result, ArrayBufferWriter<byte> buffer, Utf8JsonWriter writer)
    {
        buffer.ResetWrittenCount();
        writer.Reset(buffer);
        JsonSerializer.Serialize(writer, result, FileManagerJsonContext.Default.DryRunFileResult);
        return buffer.WrittenCount;
    }

    // Fixed structural overhead (braces, property names, enum text, quotes) — generous so it is a
    // true upper bound alongside the 6-bytes-per-char string bound.
    private const int ResultStructuralBytes = 320;
    private const int TargetStructuralBytes = 128;

    private static long UpperBoundBytes(DryRunFileResult result)
    {
        long bytes = ResultStructuralBytes;
        bytes += StringUpperBound(result.SourcePath);
        bytes += StringUpperBound(result.SourceRoot);
        bytes += StringUpperBound(result.DecidingFilter);
        bytes += StringUpperBound(result.SourceDisposition);
        foreach (string command in result.ExpandedCommands)
            bytes += StringUpperBound(command);
        foreach (DryRunTargetAction target in result.Targets)
        {
            bytes += TargetStructuralBytes;
            bytes += StringUpperBound(target.TargetPath);
            bytes += StringUpperBound(target.TargetRoot);
            bytes += StringUpperBound(target.Detail);
        }
        return bytes;
    }

    // JSON's absolute worst case is 6 UTF-8 bytes per UTF-16 code unit (\uXXXX, incl. surrogates),
    // plus the surrounding quotes — a true upper bound regardless of escaping or non-ASCII content.
    private static long StringUpperBound(string? value) => value is null ? 0 : (long)value.Length * 6 + 2;

    private static long DestinationUpperBoundBytes(DryRunDestinationEntry entry)
    {
        long bytes = TargetStructuralBytes;
        bytes += StringUpperBound(entry.TargetPath);
        bytes += StringUpperBound(entry.TargetRoot);
        bytes += StringUpperBound(entry.Detail);
        return bytes;
    }

    private async Task<DryRunFileResult?> EvaluateFileAsync(
        Profile profile,
        Payload payload,
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot,
        bool hasTransformers,
        CancellationToken ct)
    {
        // The scanner captures the stat snapshot for free during enumeration; only fall back to a
        // dedicated stat when it didn't (e.g. a single-file scope payload).
        FileMetadata? metadata = payload.Metadata;
        if (metadata is null)
        {
            var metadataResult = FileMetadataReader.Read(payload.SourcePath);
            if (metadataResult.TryGetError(out string? statError))
            {
                // The file vanished or turned unreadable between enumeration and stat — a race,
                // not a reportable plan item.
                logger.LogWarning("Dry-run skipping {Path}: {Error}", payload.SourcePath, statError);
                return null;
            }
            metadataResult.TryGetValue(out metadata);
        }

        string relativePath = Path.GetRelativePath(payload.SourceRoot, payload.SourcePath);
        int depth = SeparatorCount(relativePath);

        if (filtersBySourceRoot.TryGetValue(payload.SourceRoot, out CompiledFilterSet? filters))
        {
            // Normalize once here rather than per pattern rule inside the filter set — but only when
            // a pattern rule would actually consult it (the attribute filter, always present, does
            // not), so the common no-glob profile allocates no normalized string.
            string? normalized = filters.HasPatternRules ? NormalizeSeparators(relativePath) : null;
            FilterInput input = new(payload.SourcePath, relativePath, depth, metadata!, normalized);
            FilterDecision decision = filters.Evaluate(in input);
            if (!decision.Matched)
            {
                return new DryRunFileResult
                {
                    SourcePath = payload.SourcePath,
                    SourceRoot = payload.SourceRoot,
                    Disposition = DryRunFileDisposition.WouldSkipFilter,
                    DecidingFilter = decision.DecidingRule,
                };
            }
        }

        // M:1 topologies force Flatten (spec §3.1.2); otherwise the profile's TargetLayout rules.
        bool flatten = profile.TargetLayout == TargetLayout.Flatten || profile.Sources.Count > 1;
        // Only the flatten branch uses the bare file name; PreserveStructure never allocates it.
        string? fileName = flatten ? Path.GetFileName(payload.SourcePath) : null;

        List<DryRunTargetAction> targetActions = new(profile.Targets.Count);
        byte[]? cachedSourceHash = null;
        bool allUnchanged = true;

        foreach (TargetConfig target in profile.Targets)
        {
            if (ct.IsCancellationRequested)
                break;      // SimulateAsync's cancellation catch turns this into Canceled
            string prospective = flatten
                ? Path.Combine(target.Path, fileName!)
                : Path.Combine(target.Path, relativePath);

            if (hasTransformers)
            {
                // §4.10: the transformed output does not exist to hash or compare, so every
                // target outcome for a transformer profile is Unknown. (Reachable only via
                // hand-edited JSON in this slice — the UI never emits transformers.)
                targetActions.Add(new DryRunTargetAction
                {
                    TargetPath = prospective,
                    TargetRoot = target.Path,
                    Kind = DryRunTargetKind.Unknown,
                    Detail = "requires transform",
                });
                allUnchanged = false;
                continue;
            }

            (DryRunTargetAction action, cachedSourceHash) = await EvaluateTargetAsync(
                profile.Policies, payload.SourcePath, metadata!, prospective, cachedSourceHash, ct)
                .ConfigureAwait(false);
            // Stamp the destination root here, where profile.Targets is in scope (EvaluateTargetAsync
            // only knows the prospective path). Powers the Destinations-view grouping/filtering.
            targetActions.Add(action with { TargetRoot = target.Path });
            if (action.Kind != DryRunTargetKind.WouldSkipUnchanged)
                allUnchanged = false;
        }

        allUnchanged = allUnchanged && targetActions.Count > 0;

        return new DryRunFileResult
        {
            SourcePath = payload.SourcePath,
            SourceRoot = payload.SourceRoot,
            Disposition = allUnchanged ? DryRunFileDisposition.WouldSkipUnchanged : DryRunFileDisposition.WouldProcess,
            Targets = targetActions,
            // An all-unchanged job closes Skipped without committing (§3.4.1), so no disposition
            // runs — SourceDisposition is only meaningful when the file would actually process.
            SourceDisposition = allUnchanged ? null : profile.Policies.OnSuccess.ToString(),
        };
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

    /// <summary>Per-target simulation, in the executor's order: unchanged-check FIRST
    /// (spec §3.4.1, before conflict resolution), then the read-only conflict probe.</summary>
    private async Task<(DryRunTargetAction Action, byte[]? SourceHash)> EvaluateTargetAsync(
        PolicySettings policies,
        string sourcePath,
        FileMetadata metadata,
        string prospectivePath,
        byte[]? cachedSourceHash,
        CancellationToken ct)
    {
        bool finalExists = File.Exists(prospectivePath);

        if (finalExists)
        {
            var existing = FileMetadataReader.Read(prospectivePath);
            if (existing.TryGetValue(out FileMetadata? existingMeta) && existingMeta.Length == metadata.Length)
            {
                if (policies.VerificationMethod is VerificationMethod.Sha256 or VerificationMethod.XxHash128)
                {
                    if (cachedSourceHash is null)
                    {
                        var sourceHash = await hasher.HashFileToBytesAsync(sourcePath, policies.VerificationMethod, ct).ConfigureAwait(false);
                        if (sourceHash.IsCanceled)
                            // Placeholder — discarded by SimulateAsync's cancellation catch.
                            return (new DryRunTargetAction
                            {
                                TargetPath = prospectivePath,
                                Kind = DryRunTargetKind.Unknown,
                                Detail = "canceled",
                            }, cachedSourceHash);
                        if (sourceHash.TryGetError(out JobError? hashError))
                            return (new DryRunTargetAction
                            {
                                TargetPath = prospectivePath,
                                Kind = DryRunTargetKind.Unknown,
                                Detail = $"could not hash the source: {hashError.Message}",
                            }, null);
                        sourceHash.TryGetValue(out cachedSourceHash);
                    }

                    var targetHash = await hasher.HashFileToBytesAsync(prospectivePath, policies.VerificationMethod, ct).ConfigureAwait(false);
                    if (targetHash.IsCanceled)
                        // Placeholder — discarded by SimulateAsync's cancellation catch.
                        return (new DryRunTargetAction
                        {
                            TargetPath = prospectivePath,
                            Kind = DryRunTargetKind.Unknown,
                            Detail = "canceled",
                        }, cachedSourceHash);
                    if (targetHash.TryGetError(out JobError? targetHashError))
                        return (new DryRunTargetAction
                        {
                            TargetPath = prospectivePath,
                            Kind = DryRunTargetKind.Unknown,
                            Detail = $"could not hash the existing target: {targetHashError.Message}",
                        }, cachedSourceHash);
                    targetHash.TryGetValue(out byte[]? existingHash);

                    if (cachedSourceHash is not null && existingHash is not null
                        && existingHash.AsSpan().SequenceEqual(cachedSourceHash))
                        return (new DryRunTargetAction
                        {
                            TargetPath = prospectivePath,
                            Kind = DryRunTargetKind.WouldSkipUnchanged,
                            Detail = $"identical content ({policies.VerificationMethod})",
                        }, cachedSourceHash);
                }
                else if (existingMeta.LastWritten == metadata.LastWritten)
                {
                    // VerificationMethod.None: best-effort size + mtime equality (spec §3.4.1).
                    return (new DryRunTargetAction
                    {
                        TargetPath = prospectivePath,
                        Kind = DryRunTargetKind.WouldSkipUnchanged,
                        Detail = "same size and modified time (best-effort — VerificationMethod is None)",
                    }, cachedSourceHash);
                }
            }
        }

        var probe = conflictResolver.Probe(prospectivePath, policies.ConflictResolution, metadata.LastWritten);
        if (probe.TryGetError(out JobError? probeError))
            return (new DryRunTargetAction
            {
                TargetPath = prospectivePath,
                Kind = DryRunTargetKind.Unknown,
                Detail = probeError.Message,
            }, cachedSourceHash);
        probe.TryGetValue(out ConflictOutcome? outcome);

        if (outcome!.Action == ConflictAction.SkipExistingKept)
            return (new DryRunTargetAction
            {
                TargetPath = prospectivePath,
                Kind = DryRunTargetKind.WouldSkipConflict,
                Detail = $"existing file kept ({policies.ConflictResolution})",
            }, cachedSourceHash);

        if (!string.Equals(outcome.FinalPath, prospectivePath, StringComparison.OrdinalIgnoreCase))
            return (new DryRunTargetAction
            {
                TargetPath = prospectivePath,
                Kind = DryRunTargetKind.WouldRenameTo,
                Detail = outcome.FinalPath,
            }, cachedSourceHash);

        if (finalExists)
            return (new DryRunTargetAction
            {
                TargetPath = prospectivePath,
                Kind = DryRunTargetKind.WouldOverwrite,
                Detail = $"existing file last modified {File.GetLastWriteTimeUtc(prospectivePath):yyyy-MM-dd HH:mm:ss} UTC",
            }, cachedSourceHash);

        return (new DryRunTargetAction
        {
            TargetPath = prospectivePath,
            Kind = DryRunTargetKind.WouldWrite,
        }, cachedSourceHash);
    }
}
