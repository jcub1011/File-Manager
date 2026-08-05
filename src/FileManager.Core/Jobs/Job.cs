using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Jobs;

public enum JobErrorCode
{
    SelfPathTarget, InsufficientDiskSpace, SourceDisposed, SourceUnreadable,
    TransformerFailed, TransformerTimeout, TargetWriteFailed, VerificationMismatch,
    StagingFailed, PlacementFailed, DispositionFailed, RollbackIncomplete,
    JournalWriteFailed, LockTimeout, MetadataConflict, ConflictUnresolvable
}

public sealed record JobError
{
    public required JobErrorCode Code { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
    public int? TargetIndex { get; init; }
}

public readonly record struct JobId(Guid Value)
{
    public static JobId New() => new(Guid.CreateVersion7());       // time-ordered — sorts by creation
    public string Short => Value.ToString("N")[..8];               // temp-file suffix (§4.6)
}

/// <summary>Absolute, Path.GetFullPath-canonicalized, trailing-separator-trimmed.
/// Equality/hash: OrdinalIgnoreCase on Windows. Comparison defines the global lock order (I-LOCK-ORDER).</summary>
public readonly record struct NormalizedPath : IComparable<NormalizedPath>
{
    // Filesystem case-sensitivity is a per-OS fact, not an OS code path — this is the one
    // sanctioned OperatingSystem probe in Core (§1 rule 2 note).
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The <see cref="StringComparer"/> matching <see cref="Comparison"/>, for containers
    /// keyed on <see cref="Value"/> strings (e.g. the sweep's survivor set) that must agree exactly
    /// with this type's equality.</summary>
    internal static StringComparer ValueComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private NormalizedPath(string value) => Value = value;

    public string Value { get; }

    public static Result<NormalizedPath, JobError> Create(string path)
    {
        // JobErrorCode has no dedicated malformed-path member (§5.4 is spec-frozen);
        // SourceUnreadable is the generic "this path is unusable" bucket — callers key off
        // the message, and the validator re-wraps failures as PROFILE_PATH_INVALID.
        if (string.IsNullOrWhiteSpace(path))
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = "path is empty", Path = path };

        // Strip extended-length prefixes BEFORE canonicalizing: GetFullPath preserves them, so
        // "\\?\C:\data" and "C:\data" (one physical location) would otherwise normalize to unequal
        // keys and defeat every identity built on this type — path locks, session priorities,
        // self-write suppression. "\\?\" also disables GetFullPath's normalization entirely.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            path = string.Concat(@"\\", path.AsSpan(8));
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            path = path[4..];

        if (!System.IO.Path.IsPathFullyQualified(path))
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = $"path is not absolute: \"{path}\"", Path = path };
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            return new NormalizedPath(System.IO.Path.TrimEndingDirectorySeparator(full));
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException or NotSupportedException)
        {
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = $"malformed path \"{path}\": {ex.Message}", Path = path };
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return new JobError { Code = JobErrorCode.SourceUnreadable, Message = $"malformed path \"{path}\": {ex.GetType().Name}: {ex.Message}", Path = path };
        }
    }

    /// <summary>Wraps an already-absolute, already-canonical full path (e.g. one produced by a
    /// directory enumeration that descended from a NormalizedPath root) WITHOUT re-running
    /// Path.GetFullPath — the expensive re-canonicalization <see cref="Create"/> performs. The caller
    /// guarantees the path is fully-qualified and canonical; only a trailing separator is trimmed
    /// (a no-op — hence allocation-free — for enumerated file/dir names, which never carry one). Use
    /// <see cref="Create"/> for any path from an untrusted or external source.</summary>
    public static NormalizedPath FromCanonical(string canonicalFullPath) =>
        new(System.IO.Path.TrimEndingDirectorySeparator(canonicalFullPath));

    public bool Equals(NormalizedPath other) => string.Equals(Value, other.Value, Comparison);

    public override int GetHashCode() =>
        Value is null ? 0 : string.GetHashCode(Value, Comparison);

    public int CompareTo(NormalizedPath other) =>
        string.Compare(Value, other.Value, Comparison);

    /// <summary>Strict containment: true when this path is inside <paramref name="ancestor"/>
    /// (never for equal paths). Boundary-safe: "C:\ab" is not under "C:\a".</summary>
    public bool IsUnder(NormalizedPath ancestor)
    {
        if (Value is null || ancestor.Value is null)
            return false;
        if (Value.Length <= ancestor.Value.Length)
            return false;
        if (!Value.StartsWith(ancestor.Value, Comparison))
            return false;
        // Ancestor may itself end in a separator only when it is a volume root ("C:\").
        char boundary = Value[ancestor.Value.Length];
        return ancestor.Value[^1] == System.IO.Path.DirectorySeparatorChar
            || ancestor.Value[^1] == System.IO.Path.AltDirectorySeparatorChar
            || boundary == System.IO.Path.DirectorySeparatorChar
            || boundary == System.IO.Path.AltDirectorySeparatorChar;
    }

    /// <summary>Span twin of "<c>path.Equals(root) || path.IsUnder(root)</c>" for probe paths that
    /// were never materialized as strings (the destination sweep's per-file exclusion check).
    /// <paramref name="canonicalPath"/> must be canonical with no trailing separator — the same
    /// contract <see cref="FromCanonical"/> documents. Kept next to <see cref="IsUnder"/> so the
    /// boundary logic cannot drift between the two.</summary>
    internal static bool IsEqualToOrUnder(ReadOnlySpan<char> canonicalPath, NormalizedPath ancestor)
    {
        if (ancestor.Value is null)
            return false;
        if (canonicalPath.Equals(ancestor.Value, Comparison))
            return true;
        if (canonicalPath.Length <= ancestor.Value.Length)
            return false;
        if (!canonicalPath.StartsWith(ancestor.Value, Comparison))
            return false;
        char boundary = canonicalPath[ancestor.Value.Length];
        return ancestor.Value[^1] == System.IO.Path.DirectorySeparatorChar
            || ancestor.Value[^1] == System.IO.Path.AltDirectorySeparatorChar
            || boundary == System.IO.Path.DirectorySeparatorChar
            || boundary == System.IO.Path.AltDirectorySeparatorChar;
    }

    public override string ToString() => Value ?? string.Empty;
}

public enum TriggerKind { Watcher, Schedule, CatchUp, ManualShell, Cli }

/// <summary><paramref name="Metadata"/> is the stat snapshot captured for free during enumeration
/// (see <see cref="FileManager.Core.Files.FileSystemEntry"/>); consumers that have it can skip a
/// redundant source stat. Null when the payload was produced without an enumeration snapshot (e.g. a
/// single-file scope), in which case consumers stat on demand.</summary>
/// <param name="RunId">The manual run this payload belongs to, when it belongs to one. Lets a
/// run-scoped consumer count its own jobs to completion (the Mirror deletion barrier) and drop its own
/// pending work on cancellation. Engine-internal and additive: <see cref="Payload"/> is never
/// journalled (<c>JobOpenedRecord</c> snapshots a <see cref="SourceSnapshot"/>, not a payload) and
/// never on the wire, so this is not a schema change.</param>
public sealed record Payload(
    Guid ProfileId, string SourcePath, string SourceRoot, TriggerKind Trigger, DateTimeOffset EnqueuedAt,
    FileMetadata? Metadata = null,
    Guid? RunId = null);

/// <summary>Immutable snapshot at journal-open; recovery compares the filesystem against it.</summary>
public sealed record SourceSnapshot
{
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
}

public sealed record TargetPlan
{
    public required int TargetIndex { get; init; }
    public required string TargetRoot { get; init; }
    /// <summary>TargetLayout-resolved destination, before conflict-resolution suffixing.</summary>
    public required string ProspectiveFinalPath { get; init; }
}

/// <summary>The policy fields the journal snapshots so recovery never depends on a profile edit.</summary>
public sealed record PolicySnapshot
{
    public required VerificationMethod Verification { get; init; }
    public required OverwriteHandling OverwriteHandling { get; init; }
    public required ConflictResolution ConflictResolution { get; init; }
    public required OnSuccessAction OnSuccess { get; init; }
    public string? ArchiveFolder { get; init; }
    public required MetadataOnConflict MetadataOnConflict { get; init; }

    /// <summary>Identity policy for the §3.4.1 unchanged-check on large files. Non-required so existing
    /// plan construction (test fixtures, recovery) keeps compiling on the exact default,
    /// <see cref="LargeFileIdentity.FullHash"/>.</summary>
    public LargeFileIdentity LargeFileIdentity { get; init; }

    /// <summary>Size above which <see cref="LargeFileIdentity"/> applies. Defaults to the contract's
    /// <see cref="PolicySettings.DefaultLargeFileIdentityThresholdBytes"/> rather than 0, so a snapshot
    /// built without it cannot accidentally mean "every file is large".</summary>
    public long LargeFileIdentityThresholdBytes { get; init; }
        = PolicySettings.DefaultLargeFileIdentityThresholdBytes;
}

public sealed record JobPlan
{
    public required JobId JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required Profile Profile { get; init; }                 // full snapshot for the executor
    public required Payload Payload { get; init; }
    public required SourceSnapshot Source { get; init; }
    public required FileMetadata SourceMetadata { get; init; }     // existing Core.Files type
    public required IReadOnlyList<TargetPlan> Targets { get; init; }
    public required PolicySnapshot Policies { get; init; }
    public required string WorkspaceDir { get; init; }             // <tempRoot>/.pipeline_tmp/<JobId>/

    /// <summary>Index of the payload's originating Source within <see cref="Profile"/>, or -1 when the
    /// payload's root matches none of them. Resolved ONCE, by the factory that already has both the
    /// profile and the payload: the M:1 priority rank (spec §3.4) has to be the same value where it is
    /// written to <see cref="Placement.SourcePriorityRegistry"/> and where it is read back by
    /// <see cref="Placement.IConflictResolver.Resolve"/>, and deriving it independently on each side is
    /// how those two silently disagreed.</summary>
    public required int SourceIndex { get; init; }

    /// <summary>The priority rank to compare against the registry: an unmatched source ranks highest,
    /// which is the conservative choice — it never lets an unrecognized root evict a known one.</summary>
    public int PriorityIndex => SourceIndex < 0 ? 0 : SourceIndex;
}

/// <summary>Sealed transform output — the reference every verification compares against (§7.1 state OutputSealed).</summary>
public sealed record SealedOutput
{
    public required string Path { get; init; }         // workspace artifact; == source path when no transformers
    public required long SizeBytes { get; init; }
    // Hashed under the job's Verification method; "" when VerificationMethod.None.
    public required string ContentHash { get; init; }
    public required DateTimeOffset SourceLastWriteUtc { get; init; }
}

/// <summary>Mutable per-job execution state, owned by one pool worker; wraps the plan,
/// the state machine, the lock set, suppression tokens, and per-target progress.
/// <para><see cref="States"/> and <see cref="Targets"/> are derived from <see cref="Plan"/> and built
/// EAGERLY in its <c>init</c> accessor, which runs once on the constructing thread before the
/// execution is published. They must not be lazily built on first access: <c>JobExecutor</c> fans out
/// one task per target and every one of them touches <see cref="Targets"/> for the first time
/// concurrently, so a <c>??=</c> here let several threads each build their own array. Only the last
/// write survived, and every target that mutated a discarded <see cref="TargetProgress"/> was still
/// <see cref="TargetState.Pending"/> when rollback read it — so rollback skipped it, leaving its temp
/// on disk and, for a target that had already been placed, its content at the destination while
/// reporting a clean rollback.</para></summary>
public sealed class JobExecution
{
    private readonly JobPlan _plan = null!;
    private readonly JobStateMachine _states = null!;
    private readonly IReadOnlyList<TargetProgress> _targets = null!;

    public required JobPlan Plan
    {
        get => _plan;
        init
        {
            _plan = value;
            _states = new JobStateMachine(value.JobId);
            _targets = BuildTargets(value);
        }
    }

    public JobStateMachine States => _states;

    public SealedOutput? Output { get; set; }

    public IReadOnlyList<TargetProgress> Targets => _targets;

    private static IReadOnlyList<TargetProgress> BuildTargets(JobPlan plan)
    {
        var targets = new TargetProgress[plan.Targets.Count];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = new TargetProgress { Plan = plan.Targets[i], State = TargetState.Pending };
        return targets;
    }
}

public sealed class TargetProgress
{
    public required TargetPlan Plan { get; init; }
    public TargetState State { get; set; }
    public string? TempPath { get; set; }
    public string? FinalPath { get; set; }             // post-conflict-resolution
    public string? StagedPath { get; set; }
    public bool FinalExistedBeforeJob { get; set; }
}

public enum JobState
{
    Ingested, Locked, Opened, Preflighted, Screened, Transforming,
    OutputSealed, Distributing, Committed, Disposing, RollingBack, Closed
}

public enum TargetState
{
    Pending, SatisfiedUnchanged, SkippedConflict, TempWriting, TempWritten,
    Verified, Staged, Placed, RolledBack, RollbackFailed
}

public enum JobOutcome { Succeeded, Skipped, Failed, RollbackFailed }

public enum SkipReason { Filtered, SourceDisposed, UnchangedAtAllTargets }

public sealed record JobCompletion(
    JobId JobId, JobOutcome Outcome, SkipReason? SkipReason, JobError? Error, TimeSpan Duration)
{
    /// <summary>Paths the rollback sweep could not revert (§4.7, §7.3) — they need manual
    /// remediation, so the orchestrator forwards them onto <c>JobFailedEvent.ResidualPaths</c>.
    /// Non-empty only for <see cref="JobOutcome.RollbackFailed"/>, and empty even then when rollback
    /// failed before it could enumerate residuals (e.g. its own journal append failed).</summary>
    public IReadOnlyList<string> ResidualPaths { get; init; } = [];

    /// <summary>The Phase-6 <c>OnSuccess</c> failure (§4 Phase 6) when there was one: the copies are
    /// safe and the job genuinely closed <see cref="JobOutcome.Succeeded"/>, but the source was NOT
    /// disposed of as the profile asked — or it was, and its audit row could not be written. Set only
    /// on the Succeeded path. The journal records it in <c>job-closed</c>; this carries it out to the
    /// orchestrator, which is the only component that can put it in front of the user.</summary>
    public string? DispositionError { get; init; }

    /// <summary>The destination paths this job actually resolved, one per target, AFTER conflict
    /// resolution.
    /// <para>Feeds the Mirror deletion pass's self-write guard, and it is a structural guard rather
    /// than a nicety: conflict resolution runs under the path lock at execution time and can
    /// legitimately choose a different final path than the plan's read-only probe predicted (a
    /// <c>RenameSuffix</c> candidate that became occupied in between). Such a path is absent from the
    /// plan's survivor set, so without this list it would look like an orphan that the very same run
    /// had just created.</para>
    /// <para>Empty for a job that never reached distribution.</para></summary>
    public IReadOnlyList<string> ResolvedFinalPaths { get; init; } = [];
}

/// <summary>One intra-job progress sample, pushed by the executor at each §4.3 phase boundary and
/// after each target settles. Wire-agnostic on purpose: <see cref="JobProgressPublisher"/> turns it
/// into a <c>JobProgressEvent</c>, so the phase algorithm never touches the IPC contract.</summary>
public readonly record struct JobProgress(JobPhase Phase, int TargetsCompleted, int TargetCount);