using FileManager.Core.Jobs;

namespace FileManager.Core.Tests.Jobs;

public sealed class JobStateMachineTests
{
    private static JobStateMachine New() => new(JobId.New());

    [Fact]
    public void Happy_path_job_transitions_are_legal()
    {
        JobStateMachine m = New();
        foreach (JobState state in new[]
        {
            JobState.Locked, JobState.Opened, JobState.Preflighted, JobState.Screened,
            JobState.Transforming, JobState.OutputSealed, JobState.Distributing,
            JobState.Committed, JobState.Disposing, JobState.Closed,
        })
        {
            m.Transition(state);
            Assert.Equal(state, m.State);
        }
    }

    [Fact]
    public void Illegal_job_jump_throws()
    {
        JobStateMachine m = New();
        Assert.Throws<InvalidOperationException>(() => m.Transition(JobState.Committed));
    }

    [Fact]
    public void Rollback_is_reachable_from_distributing_but_not_after_commit()
    {
        JobStateMachine m = New();
        foreach (JobState s in new[] { JobState.Locked, JobState.Opened, JobState.Preflighted, JobState.Screened, JobState.Transforming, JobState.OutputSealed, JobState.Distributing })
            m.Transition(s);
        m.Transition(JobState.RollingBack);          // legal from Distributing
        m.Transition(JobState.Closed);

        JobStateMachine committed = New();
        foreach (JobState s in new[] { JobState.Locked, JobState.Opened, JobState.Preflighted, JobState.Screened, JobState.Transforming, JobState.OutputSealed, JobState.Distributing, JobState.Committed })
            committed.Transition(s);
        Assert.Throws<InvalidOperationException>(() => committed.Transition(JobState.RollingBack));
    }

    [Fact]
    public void Target_happy_path_transitions_are_legal()
    {
        JobStateMachine m = New();
        foreach (TargetState s in new[] { TargetState.TempWriting, TargetState.TempWritten, TargetState.Verified, TargetState.Staged, TargetState.Placed })
            m.TransitionTarget(0, s);
        Assert.Equal(TargetState.Placed, m.TargetStates[0]);
    }

    [Fact]
    public void Illegal_target_jump_throws()
    {
        JobStateMachine m = New();
        Assert.Throws<InvalidOperationException>(() => m.TransitionTarget(0, TargetState.Placed));
    }

    [Fact]
    public void Closed_is_terminal()
    {
        JobStateMachine m = New();
        m.Transition(JobState.Locked);
        m.Transition(JobState.Closed);   // Locked → Closed (SourceDisposed skip)
        foreach (JobState s in Enum.GetValues<JobState>())
            Assert.Throws<InvalidOperationException>(() => m.Transition(s));
    }

    [Fact]
    public void An_unchanged_target_cannot_be_reverted()
    {
        JobStateMachine m = New();
        m.TransitionTarget(0, TargetState.SatisfiedUnchanged);
        Assert.Throws<InvalidOperationException>(() => m.TransitionTarget(0, TargetState.RolledBack));
    }
}
