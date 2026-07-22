using System;
using System.Collections.Generic;

namespace FileManager.Core.Jobs;

/// <summary>Enforces the §7.1 job/target lifecycle. Every transition is checked against the
/// legality tables below; an illegal move is programmer error and throws
/// <see cref="InvalidOperationException"/> (spec §5.4) rather than returning a value — the
/// executor drives valid sequences, so a rejected transition means a coding bug, not a runtime
/// failure. The machine is the executor's guard; per-target placement/rollback work off the
/// mutable <see cref="TargetProgress"/> mirror.</summary>
public sealed class JobStateMachine(JobId jobId)
{
    // Legal job-state transitions, faithful to the §7.1 state diagram. RollingBack is reachable
    // from every post-Opened, pre-Committed state ("any failure after Opened"); preflight/filter
    // outcomes close directly (nothing written, I-WAL); after Committed there is no rollback
    // (I-DISPOSE — a disposition failure is logged, never reverted).
    private static readonly IReadOnlyDictionary<JobState, JobState[]> JobEdges =
        new Dictionary<JobState, JobState[]>
        {
            [JobState.Ingested] = [JobState.Locked],
            [JobState.Locked] = [JobState.Opened, JobState.Closed],
            [JobState.Opened] = [JobState.Preflighted, JobState.RollingBack, JobState.Closed],
            [JobState.Preflighted] = [JobState.Screened, JobState.RollingBack, JobState.Closed],
            [JobState.Screened] = [JobState.Transforming, JobState.RollingBack],
            [JobState.Transforming] = [JobState.OutputSealed, JobState.RollingBack],
            [JobState.OutputSealed] = [JobState.Distributing, JobState.RollingBack],
            [JobState.Distributing] = [JobState.Committed, JobState.RollingBack],
            [JobState.Committed] = [JobState.Disposing, JobState.Closed],
            [JobState.Disposing] = [JobState.Closed],
            [JobState.RollingBack] = [JobState.Closed],
            [JobState.Closed] = [],
        };

    // Legal per-target transitions (§7.1): Pending → TempWriting → TempWritten → Verified →
    // [Staged] → Placed, with short-circuit exits (SatisfiedUnchanged, SkippedConflict) and
    // failure exits (RolledBack, RollbackFailed). An unchanged/skipped/already-rolled-back target
    // is terminal — rollback never reverts it.
    private static readonly IReadOnlyDictionary<TargetState, TargetState[]> TargetEdges =
        new Dictionary<TargetState, TargetState[]>
        {
            [TargetState.Pending] = [TargetState.TempWriting, TargetState.SatisfiedUnchanged, TargetState.SkippedConflict, TargetState.RolledBack],
            [TargetState.TempWriting] = [TargetState.TempWritten, TargetState.RolledBack, TargetState.RollbackFailed],
            [TargetState.TempWritten] = [TargetState.Verified, TargetState.RolledBack, TargetState.RollbackFailed],
            [TargetState.Verified] = [TargetState.Staged, TargetState.Placed, TargetState.RolledBack, TargetState.RollbackFailed],
            [TargetState.Staged] = [TargetState.Placed, TargetState.RolledBack, TargetState.RollbackFailed],
            [TargetState.Placed] = [TargetState.RolledBack, TargetState.RollbackFailed],
            [TargetState.SatisfiedUnchanged] = [],
            [TargetState.SkippedConflict] = [],
            [TargetState.RolledBack] = [],
            [TargetState.RollbackFailed] = [],
        };

    private readonly List<TargetState> _targetStates = [];
    private JobState _state = JobState.Ingested;

    public JobId JobId => jobId;

    public JobState State => _state;

    public IReadOnlyList<TargetState> TargetStates => _targetStates;

    public void Transition(JobState to)
    {
        if (!Array.Exists(JobEdges[_state], s => s == to))
            throw new InvalidOperationException(
                $"Job {jobId.Short}: illegal state transition {_state} → {to}.");
        _state = to;
    }

    /// <summary>Transitions one target's state, growing the target list with <see cref="TargetState.Pending"/>
    /// entries as needed so a target can be referenced by index before its first move.</summary>
    public void TransitionTarget(int targetIndex, TargetState to)
    {
        if (targetIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        while (_targetStates.Count <= targetIndex)
            _targetStates.Add(TargetState.Pending);

        TargetState from = _targetStates[targetIndex];
        if (!Array.Exists(TargetEdges[from], s => s == to))
            throw new InvalidOperationException(
                $"Job {jobId.Short}: illegal target[{targetIndex}] transition {from} → {to}.");
        _targetStates[targetIndex] = to;
    }
}
