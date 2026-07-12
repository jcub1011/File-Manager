using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Scheduling;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileManager.Core.Profiles;

/// <summary>The full §4.1 validation code table. Pragmatic depth in this slice, documented per
/// code below: transformer checks are structural only (argument/token/allowlist validation
/// lands with the transformers subsystem), and cron validation is syntax-only via
/// <see cref="CronExpression"/>.</summary>
public sealed class ProfileValidator(ILogger<ProfileValidator> logger, IFilterCompiler filterCompiler) : IProfileValidator
{
    private const int SupportedSchemaVersion = 2;

    private static readonly string[] InfrastructureSegments = [".pipeline_tmp", ".fm_staging"];

    public IReadOnlyList<ValidationIssue> Validate(
        Profile candidate, IReadOnlyList<Profile> otherActiveProfiles)
    {
        List<ValidationIssue> issues = [];

        if (candidate.SchemaVersion != SupportedSchemaVersion)
            issues.Add(Error("PROFILE_SCHEMA_VERSION",
                $"Unknown SchemaVersion {candidate.SchemaVersion}; this build supports {SupportedSchemaVersion}."));

        CheckReservedValues(candidate, issues);

        if (candidate.Sources.Count == 0)
            issues.Add(Error("PROFILE_NO_SOURCES", "The profile has no Sources."));
        if (candidate.Targets.Count == 0)
            issues.Add(Error("PROFILE_NO_TARGETS", "The profile has no Targets."));

        List<NormalizedPath> sources = NormalizePaths(
            candidate.Sources.Select(s => s.Path), "Source", issues);
        List<NormalizedPath> targets = NormalizePaths(
            candidate.Targets.Select(t => t.Path), "Target", issues);
        NormalizedPath? archive = null;
        if (!string.IsNullOrWhiteSpace(candidate.Policies.ArchiveFolder))
        {
            List<NormalizedPath> archives = NormalizePaths(
                [candidate.Policies.ArchiveFolder], "Archive", issues);
            if (archives.Count == 1)
                archive = archives[0];
        }

        foreach (NormalizedPath target in targets)
        {
            if (sources.Contains(target))
                issues.Add(Error("PROFILE_TARGET_EQUALS_SOURCE",
                    $"Target \"{target.Value}\" equals a Source path of the same profile."));
        }

        CheckTransformers(candidate, issues);
        CheckFilters(candidate, issues);
        CheckSchedule(candidate, issues);

        if (candidate.Policies.OnSuccess == OnSuccessAction.MoveToArchive
            && string.IsNullOrWhiteSpace(candidate.Policies.ArchiveFolder))
            issues.Add(Error("PROFILE_ARCHIVE_MISSING",
                "OnSuccess is MoveToArchive but no ArchiveFolder is configured."));

        CheckVerificationPolicy(candidate, issues);
        CheckCrossProfile(candidate, sources, targets, otherActiveProfiles, issues);

        logger.LogDebug("Validated profile {ProfileId} ({Name}): {IssueCount} issues",
            candidate.Id, candidate.Name, issues.Count);
        return issues;
    }

    private static void CheckReservedValues(Profile candidate, List<ValidationIssue> issues)
    {
        // SyncMode.Mirror is selectable and fully modeled by the dry run, but the executor does not
        // yet perform destination deletion — the run entry point guards against actually running one
        // (see the run-profile handler). It is intentionally NOT reserved here so a Mirror profile
        // can be saved and dry-run-previewed.
        if (candidate.Policies.VerificationMethod == VerificationMethod.SizeTimestamp)
            issues.Add(Error("PROFILE_RESERVED_VALUE",
                "VerificationMethod \"SizeTimestamp\" is reserved for a future release."));
        if (candidate.Transformers is not null)
        {
            foreach (TransformerStep step in candidate.Transformers)
            {
                if (step.ArgumentMode == ArgumentMode.Shell)
                    issues.Add(Error("PROFILE_RESERVED_VALUE",
                        $"Transformer step {step.Step}: ArgumentMode \"Shell\" is reserved for a future release."));
            }
        }
        if (candidate.Filters?.ContentHashDedupe == true)
            issues.Add(Error("PROFILE_RESERVED_VALUE",
                "Filters.ContentHashDedupe is reserved for a future release."));
        foreach (SourceConfig source in candidate.Sources)
        {
            if (source.Filters?.ContentHashDedupe == true)
                issues.Add(Error("PROFILE_RESERVED_VALUE",
                    $"Source \"{source.Path}\": Filters.ContentHashDedupe is reserved for a future release."));
        }
    }

    private static List<NormalizedPath> NormalizePaths(
        IEnumerable<string> paths, string role, List<ValidationIssue> issues)
    {
        List<NormalizedPath> normalized = [];
        foreach (string path in paths)
        {
            var result = NormalizedPath.Create(path);
            if (result.TryGetError(out JobError? pathError))
            {
                issues.Add(Error("PROFILE_PATH_INVALID", $"{role} path: {pathError.Message}"));
                continue;
            }
            result.TryGetValue(out NormalizedPath value);

            foreach (string segment in value.Value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (InfrastructureSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
                {
                    issues.Add(Error("PROFILE_PATH_INVALID",
                        $"{role} path \"{path}\" is inside the engine's infrastructure directory \"{segment}\" (§3.2.3 rule 3)."));
                    break;
                }
            }
            normalized.Add(value);
        }
        return normalized;
    }

    /// <summary>Structural checks only in this slice: Arguments parsing (IArgumentParser),
    /// token-name validation, and the executable allowlist land with the transformers subsystem.
    /// The GUI never emits transformers, so those checks only matter for hand-edited JSON.</summary>
    private static void CheckTransformers(Profile candidate, List<ValidationIssue> issues)
    {
        if (candidate.Transformers is not { Count: > 0 } steps)
            return;

        var ordered = steps.OrderBy(s => s.Step).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Step != i + 1)
            {
                issues.Add(Error("PROFILE_TRANSFORMER_INVALID",
                    "Transformer Step numbers must be contiguous and 1-based."));
                break;
            }
        }

        foreach (TransformerStep step in steps)
        {
            if (step.OutputMode == OutputMode.NewFile && string.IsNullOrWhiteSpace(step.ExpectedOutputExtension))
                issues.Add(Error("PROFILE_TRANSFORMER_INVALID",
                    $"Transformer step {step.Step} (\"{step.Name}\"): ExpectedOutputExtension is required when OutputMode is NewFile."));
            if (!File.Exists(step.ExecutablePath))
                issues.Add(Error("PROFILE_TRANSFORMER_INVALID",
                    $"Transformer step {step.Step} (\"{step.Name}\"): ExecutablePath \"{step.ExecutablePath}\" is not an existing file."));
            if (step.TimeoutSeconds <= 0)
                issues.Add(Error("PROFILE_TRANSFORMER_INVALID",
                    $"Transformer step {step.Step} (\"{step.Name}\"): TimeoutSeconds must be positive."));
        }
    }

    private void CheckFilters(Profile candidate, List<ValidationIssue> issues)
    {
        // One compile per source (global merged with per-source), plus a global-only compile
        // when there are no sources, so bad patterns always fail at save, never mid-job.
        bool compiledAny = false;
        foreach (SourceConfig source in candidate.Sources)
        {
            compiledAny = true;
            var compiled = filterCompiler.Compile(candidate.Filters, source.Filters);
            if (compiled.TryGetError(out string? filterError))
                issues.Add(Error("PROFILE_FILTER_INVALID", $"Source \"{source.Path}\": {filterError}"));
        }
        if (!compiledAny)
        {
            var compiled = filterCompiler.Compile(candidate.Filters, null);
            if (compiled.TryGetError(out string? filterError))
                issues.Add(Error("PROFILE_FILTER_INVALID", filterError));
        }
    }

    private static void CheckSchedule(Profile candidate, List<ValidationIssue> issues)
    {
        if (candidate.Triggers.Schedule is not { Enabled: true } schedule)
            return;

        if (!CronExpression.TryParse(schedule.Cron, out _, out string? cronError))
            issues.Add(Error("PROFILE_CRON_INVALID", $"Schedule cron \"{schedule.Cron}\": {cronError}"));

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(schedule.Timezone, out _))
            issues.Add(Error("PROFILE_TIMEZONE_INVALID",
                $"Schedule timezone \"{schedule.Timezone}\" is not a known IANA or Windows timezone ID."));
    }

    private static void CheckVerificationPolicy(Profile candidate, List<ValidationIssue> issues)
    {
        if (candidate.Policies.VerificationMethod != VerificationMethod.None)
            return;
        switch (candidate.Policies.OnSuccess)
        {
            case OnSuccessAction.PermanentDelete:
                issues.Add(new ValidationIssue(ValidationSeverity.BlockingWarning, "PROFILE_UNVERIFIED_DELETE",
                    "VerificationMethod None combined with PermanentDelete can destroy the only copy of a file " +
                    "if a copy silently corrupts (§6.1). Save requires explicit acknowledgment."));
                break;
            case OnSuccessAction.MoveToTrash:
                issues.Add(Warning("PROFILE_UNVERIFIED_TRASH_WARN",
                    "VerificationMethod None combined with MoveToTrash relies on the Recycle Bin as the only safety net (§6.1)."));
                break;
        }
    }

    private static void CheckCrossProfile(
        Profile candidate,
        List<NormalizedPath> sources,
        List<NormalizedPath> targets,
        IReadOnlyList<Profile> otherActiveProfiles,
        List<ValidationIssue> issues)
    {
        // Own-profile check runs regardless of Active; cross-profile interactions only exist
        // for a profile that will actually run.
        foreach (NormalizedPath target in targets)
        {
            foreach (NormalizedPath source in sources)
            {
                if (target.Equals(source) || target.IsUnder(source) || source.IsUnder(target))
                {
                    issues.Add(Warning("PROFILE_TARGET_IN_SOURCE_WARN",
                        $"Target \"{target.Value}\" overlaps this profile's own Source \"{source.Value}\" — " +
                        "delivered files will re-trigger this profile (chaining; §3.2.3 rule 1)."));
                }
            }
        }

        if (!candidate.Active)
            return;

        var others = otherActiveProfiles
            .Where(p => p.Id != candidate.Id && p.Active)
            .Select(p => (Profile: p,
                Sources: NormalizeQuietly(p.Sources.Select(s => s.Path)),
                Targets: NormalizeQuietly(p.Targets.Select(t => t.Path))))
            .ToList();

        foreach (NormalizedPath target in targets)
        {
            foreach (var other in others)
            {
                foreach (NormalizedPath otherSource in other.Sources)
                {
                    if (target.Equals(otherSource) || target.IsUnder(otherSource) || otherSource.IsUnder(target))
                        issues.Add(Warning("PROFILE_TARGET_IN_SOURCE_WARN",
                            $"Target \"{target.Value}\" overlaps Source \"{otherSource.Value}\" of active profile " +
                            $"\"{other.Profile.Name}\" — delivered files will trigger it (chaining; §3.2.3 rule 1)."));
                }
            }
        }

        if (HasCycle(candidate, sources, targets, others))
            issues.Add(Warning("PROFILE_CYCLE_WARN",
                "The Source→Target graph across active profiles contains a cycle involving this profile (§3.2.3)."));

        bool candidateDisposes = candidate.Policies.OnSuccess != OnSuccessAction.KeepSource;
        foreach (NormalizedPath source in sources)
        {
            foreach (var other in others)
            {
                foreach (NormalizedPath otherSource in other.Sources)
                {
                    bool overlaps = source.Equals(otherSource) || source.IsUnder(otherSource) || otherSource.IsUnder(source);
                    bool otherDisposes = other.Profile.Policies.OnSuccess != OnSuccessAction.KeepSource;
                    if (overlaps && (candidateDisposes || otherDisposes))
                        issues.Add(Warning("PROFILE_OVERLAP_DISPOSAL_WARN",
                            $"Source \"{source.Value}\" overlaps Source \"{otherSource.Value}\" of active profile " +
                            $"\"{other.Profile.Name}\" and at least one of them disposes sources (§5.4)."));
                }
            }
        }
    }

    /// <summary>Cycle detection over the "my target feeds your source" edges among active
    /// profiles, reported when a cycle passes through the candidate. Other profiles' own
    /// malformed paths are ignored here — they were validated at their own save.</summary>
    private static bool HasCycle(
        Profile candidate,
        List<NormalizedPath> candidateSources,
        List<NormalizedPath> candidateTargets,
        List<(Profile Profile, List<NormalizedPath> Sources, List<NormalizedPath> Targets)> others)
    {
        var nodes = new List<(Guid Id, List<NormalizedPath> Sources, List<NormalizedPath> Targets)>
        {
            (candidate.Id, candidateSources, candidateTargets),
        };
        nodes.AddRange(others.Select(o => (o.Profile.Id, o.Sources, o.Targets)));

        static bool Feeds(List<NormalizedPath> targets, List<NormalizedPath> sources) =>
            targets.Any(t => sources.Any(s => t.Equals(s) || t.IsUnder(s) || s.IsUnder(t)));

        // BFS from every node the candidate feeds; a path back to the candidate is a cycle.
        // (A self-edge is already surfaced as PROFILE_TARGET_IN_SOURCE_WARN.)
        Queue<int> queue = new();
        HashSet<int> visited = [];
        for (int i = 1; i < nodes.Count; i++)
        {
            if (Feeds(nodes[0].Targets, nodes[i].Sources) && visited.Add(i))
                queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (Feeds(nodes[current].Targets, nodes[0].Sources))
                return true;
            for (int i = 1; i < nodes.Count; i++)
            {
                if (i != current && Feeds(nodes[current].Targets, nodes[i].Sources) && visited.Add(i))
                    queue.Enqueue(i);
            }
        }
        return false;
    }

    private static List<NormalizedPath> NormalizeQuietly(IEnumerable<string> paths)
    {
        List<NormalizedPath> normalized = [];
        foreach (string path in paths)
        {
            if (NormalizedPath.Create(path).TryGetValue(out NormalizedPath value))
                normalized.Add(value);
        }
        return normalized;
    }

    private static ValidationIssue Error(string code, string message) =>
        new(ValidationSeverity.Error, code, message);

    private static ValidationIssue Warning(string code, string message) =>
        new(ValidationSeverity.Warning, code, message);
}
