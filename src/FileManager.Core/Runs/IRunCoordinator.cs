using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Runs;

/// <summary>Owns the lifecycle of a manual run: plan it, hold it for approval, execute it, close it.
/// <para>A run is the unit the user thinks in and nothing in the engine previously modelled. Before
/// this, a 5,000-file invocation was 5,000 independent jobs with no start, no end, no totals, and no
/// way to stop it — and no point at which "every copy has landed, now remove the orphans" could be
/// expressed at all.</para></summary>
/// <summary>All <c>JobOrchestrator</c> needs from a run: somewhere to report that a payload reached a
/// terminal state, and the profile that run was planned against.
/// <para>Deliberately narrower than <see cref="IRunCoordinator"/>. The orchestrator is a hot loop that
/// has no business being able to start, approve, or cancel runs, and keeping the dependency this small
/// is also what lets a test drive the orchestrator with a no-op sink instead of standing up a
/// coordinator.</para></summary>
public interface IRunSettleSink
{
    /// <summary>Reports that a payload of <paramref name="runId"/> reached a terminal state.
    /// <paramref name="completion"/> is null when the payload was DROPPED before the executor ran (its
    /// profile vanished or went inactive, or its plan could not be built) — such a payload still
    /// settles the run's barrier, which is the entire reason this is reported at all.
    /// <para>An unknown run id is ignored, not an error: most payloads belong to no run.</para></summary>
    void Settled(Guid runId, JobCompletion? completion);

    /// <summary>The profile <paramref name="runId"/> was PLANNED against — the same one frozen into its
    /// snapshot header — or null when the id is unknown.
    /// <para>This exists because a run's copies must not be built from the live catalog. The plan the
    /// user approved was computed from this profile, and the Mirror deletion pass already reads it back
    /// from the snapshot; resolving the catalog instead meant the two halves of one run could use
    /// different targets, layouts and dispositions. It is also the only way a run planned from an
    /// unsaved draft can copy anywhere at all — that profile is in no catalog.</para></summary>
    Profile? PlannedProfile(Guid runId);
}

/// <summary>A run-settle sink that does nothing, for a host or test with no run coordinator.</summary>
public sealed class NullRunSettleSink : IRunSettleSink
{
    public static NullRunSettleSink Instance { get; } = new();
    public void Settled(Guid runId, JobCompletion? completion) { }
    /// <summary>Null: with no coordinator there is no run to have planned anything, so every caller
    /// falls back to the catalog exactly as it did before runs existed.</summary>
    public Profile? PlannedProfile(Guid runId) => null;
}

public interface IRunCoordinator : IRunSettleSink
{
    /// <summary>Starts planning a run and returns as soon as the run exists, so an IPC caller is not
    /// blocked behind a scan (§8 rule 5). Planning proceeds on a background task; the run reaches
    /// <see cref="RunPhase.AwaitingApproval"/> when it finishes.</summary>
    Result<RunHandle, string> Begin(Profile profile, string? scopePath);

    /// <summary>Approves a planned run (starts execution) or declines it (closes it, changing nothing).
    /// Only valid while the run is <see cref="RunPhase.AwaitingApproval"/>.</summary>
    Result Approve(Guid runId, bool approve);

    /// <summary>Cancels a run at any phase. Planning stops; pending payloads are dropped; the deletion
    /// phase is skipped. Jobs already in flight are NEVER interrupted (I-ATOMIC-JOB) — the run closes
    /// once they drain.</summary>
    Result Cancel(Guid runId);

    /// <summary>The run's current state, or null when the id is unknown (an unknown id is a normal
    /// case: runs are forgotten a while after they close).</summary>
    RunStatus? GetStatus(Guid runId);

    /// <summary>Reports that a payload will never produce a job because the queue superseded it, so the
    /// barrier stops waiting for one.</summary>
    void Coalesced(Guid runId);

    /// <summary>The directory holding a run's snapshot, for the plan-replay handler.</summary>
    string? SnapshotDirectory(Guid runId);

    /// <summary>Stops accepting new runs and waits for in-flight ones to unwind.</summary>
    Task StopAsync();
}

/// <summary>Where a run is in its lifecycle. Declaration order is the progression.</summary>
public enum RunPhase
{
    /// <summary>Scanning and evaluating: building the work list.</summary>
    Planning,
    /// <summary>The work list is frozen and waiting for the user to approve or decline it. NOTHING has
    /// been touched at this point.</summary>
    AwaitingApproval,
    /// <summary>Copying, and (for Mirror) removing orphans.</summary>
    Executing,
    /// <summary>Terminal.</summary>
    Closed,
}

/// <summary>How a run ended.</summary>
public enum RunOutcome
{
    /// <summary>Not ended yet.</summary>
    None,
    /// <summary>Every planned item was attempted and none failed.</summary>
    Succeeded,
    /// <summary>Ran to the end, but at least one job failed or at least one orphan was not removed.</summary>
    CompletedWithProblems,
    /// <summary>The user declined the plan, or cancelled the run.</summary>
    Cancelled,
    /// <summary>Planning itself failed, so there was never a work list to execute.</summary>
    PlanFailed,
}

/// <summary>What a caller gets back from <see cref="IRunCoordinator.Begin"/>.</summary>
public sealed record RunHandle
{
    public required Guid RunId { get; init; }
    public required Guid ProfileId { get; init; }
}

/// <summary>A run's observable state. Snapshot semantics — read once, do not expect it to update.</summary>
public sealed record RunStatus
{
    public required Guid RunId { get; init; }
    public required Guid ProfileId { get; init; }
    public required RunPhase Phase { get; init; }
    public required RunOutcome Outcome { get; init; }

    /// <summary>Copy items the plan named — the progress denominator, known up front precisely because
    /// the work list was frozen before execution began.</summary>
    public required int PlannedCopies { get; init; }

    /// <summary>Orphans the plan named. Always zero outside Mirror.</summary>
    public required int PlannedDeletes { get; init; }

    public required long PlannedCopyBytes { get; init; }
    public required long PlannedDeleteBytes { get; init; }

    /// <summary>Jobs that have reached a terminal state, by outcome.</summary>
    public required int Succeeded { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }

    /// <summary>Orphans actually removed, and the bytes they held.</summary>
    public required int Deleted { get; init; }
    public required long BytesDeleted { get; init; }

    /// <summary>Set when the plan did not cover everything it was asked to. Copies may still run (a
    /// partial copy destroys nothing); the deletion phase refuses outright.</summary>
    public required bool PlanTruncated { get; init; }

    /// <summary>Why the deletion phase removed nothing, when it refused. Null when it ran, and null
    /// outside Mirror.</summary>
    public string? DeletionAbortReason { get; init; }

    /// <summary>Why planning failed, when it did.</summary>
    public string? PlanError { get; init; }

    /// <summary>Paths a deletion attempt could not remove, if any.</summary>
    public IReadOnlyList<string> DeletionFailures { get; init; } = [];
}
