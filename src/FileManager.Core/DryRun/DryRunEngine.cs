using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using FileManager.Contracts;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    TimeProvider time) : IDryRunEngine
{
    /// <summary>Report size guards: a serialized report must fit an IPC frame (16 MiB cap, §3.1).
    /// The byte budget is the guarantee — each result is measured as serialized and the report
    /// truncates when the running total would exceed it (16 MiB minus the response envelope and
    /// headroom for future additive fields). The file-count cap is a secondary bound on UI
    /// row-building work. A paged/streamed report is a future additive Contracts change.</summary>
    internal const int MaxReportBytes = 12 * 1024 * 1024;
    internal const int MaxReportedFiles = 50_000;

    /// <summary>Test seam: production uses <see cref="MaxReportBytes"/>; tests shrink it so
    /// truncation is reachable without tens of thousands of real files.</summary>
    internal int ReportByteBudget { get; init; } = MaxReportBytes;

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
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceConfig source in profile.Sources)
        {
            var compiled = filterCompiler.Compile(profile.Filters, source.Filters);
            if (compiled.TryGetError(out string? compileError))
            {
                logger.LogError(
                    "Dry-run for profile {ProfileId} failed: filter compilation error (was this profile saved through validation?): {Error}",
                    profileId, compileError);
                return $"filter compilation failed (was this profile saved through validation?): {compileError}";
            }

            compiled.TryGetValue(out CompiledFilterSet? set);
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                filtersBySourceRoot[root.Value] = set!;
        }

        bool hasTransformers = profile.Transformers is { Count: > 0 };
        bool truncated = false;
        long reportBytes = 0;
        List<DryRunFileResult> files = [];

        // Reused across the whole scan so measuring doesn't allocate a byte[] per file; the
        // default writer options (unindented, default encoder) match FileManagerJsonContext.
        ArrayBufferWriter<byte> measureBuffer = new();
        using Utf8JsonWriter measureWriter = new(measureBuffer);

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

            if (files.Count >= MaxReportedFiles)
            {
                truncated = true;
                break;
            }

            scanned.TryGetValue(out Payload? payload);
            DryRunFileResult? result = await EvaluateFileAsync(profile, payload!, filtersBySourceRoot, hasTransformers, ct)
                .ConfigureAwait(false);
            // A hash or target loop cut short by cancellation returns a partial/placeholder result;
            // the token check turns that into Canceled rather than a misleading partial report.
            if (ct.IsCancellationRequested)
                return Result<DryRunReport, string>.Canceled();
            if (result is not null)
            {
                // Measured exactly (a standalone record serializes byte-identically to the same
                // record as a Files[] element) — path-length heuristics undercount JSON escaping
                // (`\` doubles, non-ASCII becomes 6-byte \uXXXX).
                measureBuffer.ResetWrittenCount();
                measureWriter.Reset(measureBuffer);
                JsonSerializer.Serialize(measureWriter, result, FileManagerJsonContext.Default.DryRunFileResult);
                int resultBytes = measureBuffer.WrittenCount;
                if (reportBytes + resultBytes > ReportByteBudget)
                {
                    truncated = true;
                    break;
                }
                reportBytes += resultBytes;
                files.Add(result);
            }
        }

        if (truncated)
            logger.LogWarning(
                "Dry-run report for profile {ProfileId} truncated at {FileCount} files / {Bytes:N0} bytes " +
                "(caps: {ByteCap:N0} bytes, {FileCap} files) — the scan found more",
                profileId, files.Count, reportBytes, ReportByteBudget, MaxReportedFiles);

        files.Sort(static (a, b) => string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));
        DateTimeOffset completedAt = time.GetUtcNow();
        logger.LogInformation(
            "Dry-run completed for profile {ProfileId}: {FileCount} files in {ElapsedMs}ms{Truncated}",
            profileId, files.Count, (completedAt - startedAt).TotalMilliseconds,
            truncated ? " (report truncated)" : "");
        return new DryRunReport(profileId, completedAt, files, truncated);
    }

    private async Task<DryRunFileResult?> EvaluateFileAsync(
        Profile profile,
        Payload payload,
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot,
        bool hasTransformers,
        CancellationToken ct)
    {
        var metadataResult = FileMetadataReader.Read(payload.SourcePath);
        if (metadataResult.TryGetError(out string? statError))
        {
            // The file vanished or turned unreadable between enumeration and stat — a race,
            // not a reportable plan item.
            logger.LogWarning("Dry-run skipping {Path}: {Error}", payload.SourcePath, statError);
            return null;
        }
        metadataResult.TryGetValue(out FileMetadata? metadata);

        string relativePath = Path.GetRelativePath(payload.SourceRoot, payload.SourcePath);
        int depth = relativePath.Count(static c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

        if (filtersBySourceRoot.TryGetValue(payload.SourceRoot, out CompiledFilterSet? filters))
        {
            FilterInput input = new(payload.SourcePath, relativePath, depth, metadata!);
            FilterDecision decision = filters.Evaluate(in input);
            if (!decision.Matched)
            {
                return new DryRunFileResult
                {
                    SourcePath = payload.SourcePath,
                    Disposition = DryRunFileDisposition.WouldSkipFilter,
                    DecidingFilter = decision.DecidingRule,
                };
            }
        }

        // M:1 topologies force Flatten (spec §3.1.2); otherwise the profile's TargetLayout rules.
        bool flatten = profile.TargetLayout == TargetLayout.Flatten || profile.Sources.Count > 1;
        string fileName = Path.GetFileName(payload.SourcePath);

        List<DryRunTargetAction> targetActions = [];
        string? cachedSourceHash = null;

        foreach (TargetConfig target in profile.Targets)
        {
            if (ct.IsCancellationRequested)
                break;      // SimulateAsync's post-call token check turns this into Canceled
            string prospective = flatten
                ? Path.Combine(target.Path, fileName)
                : Path.Combine(target.Path, relativePath);

            if (hasTransformers)
            {
                // §4.10: the transformed output does not exist to hash or compare, so every
                // target outcome for a transformer profile is Unknown. (Reachable only via
                // hand-edited JSON in this slice — the UI never emits transformers.)
                targetActions.Add(new DryRunTargetAction
                {
                    TargetPath = prospective,
                    Kind = DryRunTargetKind.Unknown,
                    Detail = "requires transform",
                });
                continue;
            }

            (DryRunTargetAction action, cachedSourceHash) = await EvaluateTargetAsync(
                profile.Policies, payload.SourcePath, metadata!, prospective, cachedSourceHash, ct)
                .ConfigureAwait(false);
            targetActions.Add(action);
        }

        bool allUnchanged = targetActions.Count > 0
            && targetActions.All(static t => t.Kind == DryRunTargetKind.WouldSkipUnchanged);

        return new DryRunFileResult
        {
            SourcePath = payload.SourcePath,
            Disposition = allUnchanged ? DryRunFileDisposition.WouldSkipUnchanged : DryRunFileDisposition.WouldProcess,
            Targets = targetActions,
            // An all-unchanged job closes Skipped without committing (§3.4.1), so no disposition
            // runs — SourceDisposition is only meaningful when the file would actually process.
            SourceDisposition = allUnchanged ? null : profile.Policies.OnSuccess.ToString(),
        };
    }

    /// <summary>Per-target simulation, in the executor's order: unchanged-check FIRST
    /// (spec §3.4.1, before conflict resolution), then the read-only conflict probe.</summary>
    private async Task<(DryRunTargetAction Action, string? SourceHash)> EvaluateTargetAsync(
        PolicySettings policies,
        string sourcePath,
        FileMetadata metadata,
        string prospectivePath,
        string? cachedSourceHash,
        CancellationToken ct)
    {
        bool finalExists = File.Exists(prospectivePath);

        if (finalExists)
        {
            var existing = FileMetadataReader.Read(prospectivePath);
            if (existing.TryGetValue(out FileMetadata? existingMeta) && existingMeta.Length == metadata.Length)
            {
                if (policies.VerificationMethod == VerificationMethod.Sha256)
                {
                    if (cachedSourceHash is null)
                    {
                        var sourceHash = await hasher.HashFileAsync(sourcePath, ct).ConfigureAwait(false);
                        if (sourceHash.IsCanceled)
                            // Placeholder — discarded by SimulateAsync's post-call cancellation check.
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

                    var targetHash = await hasher.HashFileAsync(prospectivePath, ct).ConfigureAwait(false);
                    if (targetHash.IsCanceled)
                        // Placeholder — discarded by SimulateAsync's post-call cancellation check.
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
                    targetHash.TryGetValue(out string? existingHash);

                    if (string.Equals(cachedSourceHash, existingHash, StringComparison.OrdinalIgnoreCase))
                        return (new DryRunTargetAction
                        {
                            TargetPath = prospectivePath,
                            Kind = DryRunTargetKind.WouldSkipUnchanged,
                            Detail = "identical content (SHA-256)",
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
