using System;
using System.Collections.Generic;

namespace FileManager.Core.Jobs;

public sealed class JobStateMachine(JobId jobId)
{
    public JobState State { get; }
    public IReadOnlyList<TargetState> TargetStates { get; }
    public void Transition(JobState to) => throw new NotImplementedException();                    // throws InvalidOperationException on illegal move
    public void TransitionTarget(int targetIndex, TargetState to) => throw new NotImplementedException();
}
