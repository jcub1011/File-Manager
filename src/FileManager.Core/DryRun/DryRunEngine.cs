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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>Report size guard: a serialized report must fit an IPC frame (16 MiB cap, §3.1).
    /// A paged/streamed report is a future additive Contracts change.</summary>
    internal const int MaxReportedFiles = 50_000;

    public async Task<Result<DryRunReport, string>> SimulateAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default)
    {
        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
            return $"profile {profileId} not found";

        // One compiled set per source, keyed by the source root the scanner stamps on payloads.
        Dictionary<string, CompiledFilterSet> filtersBySourceRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceConfig source in profile.Sources)
        {
            var compiled = filterCompiler.Compile(profile.Filters, source.Filters);
            if (compiled.TryGetError(out string? compileError))
                return $"filter compilation failed (was this profile saved through validation?): {compileError}";
            compiled.TryGetValue(out CompiledFilterSet? set);
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                filtersBySourceRoot[root.Value] = set!;
        }

        bool hasTransformers = profile.Transformers is { Count: > 0 };
        bool truncated = false;
        List<DryRunFileResult> files = [];

        foreach (var scanned in scanner.Scan(profile, TriggerKind.Cli, scopePath))
        {
            ct.ThrowIfCancellationRequested();

            if (scanned.TryGetError(out EnumerationFault fault))
            {
                if (fault.Severity == EnumerationSeverity.Fatal)
                    return $"scan failed: {fault.Message}";
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
            if (result is not null)
                files.Add(result);
        }

        if (truncated)
            logger.LogWarning(
                "Dry-run report for profile {ProfileId} truncated at {Cap} files — the scan found more",
                profileId, MaxReportedFiles);

        files.Sort(static (a, b) => string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));
        return new DryRunReport(profileId, time.GetUtcNow(), files);
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
            ct.ThrowIfCancellationRequested();
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
