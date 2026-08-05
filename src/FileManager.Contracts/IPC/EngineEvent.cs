using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.IPC;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JobStartedEvent), "job-started")]
[JsonDerivedType(typeof(JobProgressEvent), "job-progress")]
[JsonDerivedType(typeof(JobCompletedEvent), "job-completed")]
[JsonDerivedType(typeof(JobFailedEvent), "job-failed")]
[JsonDerivedType(typeof(PauseChangedEvent), "pause-changed")]
[JsonDerivedType(typeof(ProfilesChangedEvent), "profiles-changed")]
[JsonDerivedType(typeof(RunQueuedEvent), "run-queued")]
[JsonDerivedType(typeof(EngineWarningEvent), "engine-warning")]
[JsonDerivedType(typeof(RunPlannedEvent), "run-planned")]
[JsonDerivedType(typeof(RunProgressEvent), "run-progress")]
[JsonDerivedType(typeof(RunCompletedEvent), "run-completed")]
public abstract record EngineEvent
{
    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>Which §4.3 phase a job is currently in. A wire enum, 1:1 with the normative phase
/// algorithm plus the rollback sweep — deliberately NOT Core's <c>JobState</c>, which is a 12-member
/// state machine carrying guard-only transitions the UI has no use for.
/// <para>The declaration order is the phase order and is load-bearing: the progress publisher drops
/// any sample whose phase went backwards, so reordering these members changes behavior.</para></summary>
public enum JobPhase
{
    Locking, Opening, Preflighting, Screening, Sealing, Distributing, Committing, Disposing, RollingBack
}

public sealed record JobStartedEvent : EngineEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string SourcePath { get; init; }
}
/// <summary>Best-effort intra-job progress (§4.3, §4.10). Throttled and lossy by design: a job that
/// finishes inside one publish interval emits a single frame, and the terminal
/// <see cref="JobCompletedEvent"/> / <see cref="JobFailedEvent"/> — never a progress frame — is
/// authoritative for the outcome. A frame whose <see cref="JobId"/> is unknown to the consumer (its
/// job-started was dropped by a bounded subscriber channel) is simply ignored.</summary>
public sealed record JobProgressEvent : EngineEvent
{
    public required Guid JobId { get; init; }
    public required JobPhase Phase { get; init; }
    /// <summary>Targets finished — placed, already-unchanged, or conflict-skipped — out of
    /// <see cref="TargetCount"/>. Zero outside <see cref="JobPhase.Distributing"/>; never decreases.</summary>
    public required int TargetsCompleted { get; init; }
    public required int TargetCount { get; init; }
}
public sealed record JobCompletedEvent : EngineEvent { public required JobSummaryDto Job { get; init; } }
public sealed record JobFailedEvent : EngineEvent
{
    public required JobSummaryDto Job { get; init; }
    public required string Error { get; init; }
    /// <summary>The profile's Logging.NotifyOnFailure, stamped by the service at publish time so
    /// the Contracts-only tray can decide whether to raise a native notification (spec §7).</summary>
    public required bool NotifyOnFailure { get; init; }
    /// <summary>Paths the rollback sweep could not revert — these need manual remediation. Populated
    /// only for a <c>RollbackFailed</c> outcome, and empty even then when rollback failed before it
    /// could enumerate residuals (e.g. its own journal append failed), so an empty list alongside
    /// RollbackFailed is a legitimate state, not a bug.</summary>
    public IReadOnlyList<string> ResidualPaths { get; init; } = [];
}
public sealed record PauseChangedEvent : EngineEvent { public required bool Paused { get; init; } }
public sealed record ProfilesChangedEvent : EngineEvent;
/// <summary>Terminates a folder run's background enumeration (§4.9): the number of payloads actually
/// queued, or 0 for "nothing matched". Correlates with the <c>RunProfileResponse</c> that reported
/// <c>Scanning = true</c> by (<see cref="ProfileId"/>, <see cref="ScopePath"/>). Never emitted for a
/// single-file run — that reply's QueuedCount is already exact, and a second signal would
/// double-count.</summary>
public sealed record RunQueuedEvent : EngineEvent
{
    public required Guid ProfileId { get; init; }
    public required string ScopePath { get; init; }
    public required int QueuedCount { get; init; }

    /// <summary>The <see cref="RunProfileResponse.RunId"/> of the request that started this run, so a
    /// client can tell its own run's outcome from another client's. Broadcast delivery means every
    /// subscriber sees every run; only the requester should report it as theirs.</summary>
    public required Guid RunId { get; init; }
    /// <summary>Set when the enumeration aborted on a fatal fault; <see cref="QueuedCount"/> is then
    /// a partial count of what was queued before the abort.</summary>
    public string? Error { get; init; }
}
public sealed record EngineWarningEvent : EngineEvent { public required string Message { get; init; } }

/// <summary>A run has finished planning and is waiting to be approved. NOTHING has been touched yet:
/// this event is the moment the user can still say no, and the counts are what they are saying yes to.
/// <para>The itemized work list is fetched separately with <c>get-run-plan-stream</c> — it can be
/// hundreds of thousands of rows and does not belong on a broadcast event.</para></summary>
public sealed record RunPlannedEvent : EngineEvent
{
    public required Guid RunId { get; init; }
    public required Guid ProfileId { get; init; }

    /// <summary>The name of the profile this run was planned against.
    /// <para>On the event rather than looked up from <see cref="ProfileId"/> because a run planned from an
    /// unsaved draft has a profile that is in NO catalog — the lookup every other run DTO relies on
    /// returns null for exactly the runs the GUI starts. Empty only if the profile itself is unnamed.</para></summary>
    public string ProfileName { get; init; } = "";
    public required int PlannedCopies { get; init; }
    /// <summary>Destination files that will be moved to the Recycle Bin. Non-zero only under
    /// <c>SyncMode.Mirror</c>, and the most consequential number in this event.</summary>
    public required int PlannedDeletes { get; init; }
    public required long PlannedCopyBytes { get; init; }
    public required long PlannedDeleteBytes { get; init; }
    /// <summary>The plan does not cover everything it was asked to. Copies may still proceed; orphan
    /// deletion will refuse outright, because an incomplete plan's orphan list cannot be trusted.</summary>
    public required bool Truncated { get; init; }
    /// <summary>Set when planning failed outright — there is no work list, and the run is already
    /// closed.</summary>
    public string? Error { get; init; }

    /// <summary>The §4.1 blocking warnings the profile this run was planned against raises, or empty.
    /// Non-empty means <c>approve-run</c> will be REFUSED unless it carries
    /// <see cref="ApproveRunRequest.AcknowledgeWarnings"/>.
    /// <para>Carried on the event rather than fetched separately so the approval footer can state what
    /// must be acknowledged beside the rows it applies to — the same reasoning that moved the whole
    /// blast-radius warning out of a pre-run dialog and into the footer.</para></summary>
    public IReadOnlyList<ValidationIssue> BlockingIssues { get; init; } = [];
}

/// <summary>Best-effort run-level progress, during PLANNING as well as execution. Throttled and lossy
/// like <see cref="JobProgressEvent"/>;
/// <see cref="RunCompletedEvent"/> is authoritative. Once EXECUTING this has a real denominator, unlike a
/// job's progress, because the work list was frozen before execution started; while PLANNING there is no
/// denominator yet — that is what planning is computing — so the two scan counts below carry it
/// instead.</summary>
public sealed record RunProgressEvent : EngineEvent
{
    public required Guid RunId { get; init; }

    /// <summary>The <c>RunPhase</c> this sample was taken in, as a string. <c>Planning</c> means the copy
    /// counters below are all zero and the scan counts are the live figures.</summary>
    public required string Phase { get; init; }
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public required int Deleted { get; init; }

    /// <summary>Source files the plan's scan has discovered so far, during <c>Planning</c>.
    /// <para>This exists because a preview of a large tree is minutes of walking with nothing to show. The
    /// dry run that preceded the two-phase shape streamed exactly these counts and the caption read
    /// "Scanning sources… 12,345 files found"; when planning moved into the run coordinator the progress
    /// pipeline was not carried over, leaving one unchanging sentence for the whole scan — a wedged walk
    /// and a slow one look identical.</para></summary>
    public long ScannedSources { get; init; }

    /// <summary>Destination files the plan's sweep has discovered so far, during <c>Planning</c>. Stays
    /// zero for a profile that does not scan its destination, which is most of them.</summary>
    public long ScannedDestinations { get; init; }

    /// <summary>Whether this run is individually paused (<see cref="SetRunPausedRequest"/>), independent
    /// of the global engine pause.
    /// <para>Carried on the progress sample rather than as an event of its own so a paused run keeps
    /// saying so: the counters below stop moving while paused, and without this flag a paused run and a
    /// wedged one are indistinguishable — the same reason the scan counts exist.</para></summary>
    public bool Paused { get; init; }
}

/// <summary>A run reached its terminal state. The only authoritative statement of what a run did.</summary>
public sealed record RunCompletedEvent : EngineEvent
{
    public required Guid RunId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string Outcome { get; init; }
    public required int Succeeded { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public required int Deleted { get; init; }
    public required long BytesDeleted { get; init; }
    /// <summary>Why the orphan-deletion phase removed nothing. Non-null means the copies may well have
    /// succeeded while the destructive half was refused — which the user must be told, because the
    /// destination is then NOT a mirror of the source.</summary>
    public string? DeletionAbortReason { get; init; }
}
