using System;

namespace FileManager.Core.Watching;

/// <summary>A per-run pause gate, distinct from the global engine pause
/// (<see cref="IPauseStateService"/>). Implemented by the run coordinator, which owns run state; consumed
/// by <see cref="ITriggerQueue"/> and the Mirror deletion pass, which must not depend on the coordinator's
/// whole surface to ask one question.
///
/// <para><b>A per-run pause only withholds work that has not started. It never aborts anything.</b> This
/// is the deliberate semantic difference from the global pause, which the Mirror deletion pass treats as a
/// reason to abort fail-closed. The reasoning: the global pause is a safety brake ("stop touching my
/// disk"), so aborting a destructive pass under it is the conservative answer; a per-run pause is the
/// user saying "hold this one for a moment", and answering that by refusing the deletion half of a run
/// they already approved would be a surprising, silent downgrade of what they asked for. A user who wants
/// a run to stop has <c>cancel-run</c>.</para>
///
/// <para>Jobs already in flight are never interrupted either way (I-ATOMIC-JOB), so pausing an executing
/// run means "start no more copies", not "stop mid-file".</para>
///
/// <para>Deliberately narrower than <see cref="Runs.IRunCoordinator"/>, for the same reason
/// <see cref="Runs.IRunSettleSink"/> is: the trigger queue is a hot path with no business being able to
/// start, approve, or cancel runs, and a one-question interface is what lets a test drive the queue with a
/// stub instead of standing up a coordinator.</para></summary>
public interface IRunPauseGate
{
    /// <summary>Whether <paramref name="runId"/> is individually paused.
    /// <para>False for any unknown or closed id. That is load-bearing rather than lenient: a payload whose
    /// run the coordinator has already forgotten would otherwise be withheld forever, so "I do not know
    /// this run" must read as "do not withhold it". Callers with a nullable run id should not call at all —
    /// a payload belonging to no run is never withheld.</para></summary>
    bool IsRunPaused(Guid runId);

    /// <summary>Notifies of a pause transition, so a blocked consumer can re-check its gate. Handlers
    /// receive the run id and its new state, and must be O(µs) — this is raised while the coordinator
    /// holds no lock, but from whichever thread answered the IPC request.</summary>
    IDisposable Subscribe(Action<Guid, bool> pauseHandler);
}

/// <summary>A gate where nothing is ever paused, for a host or test with no run coordinator. Mirrors
/// <see cref="Runs.NullRunSettleSink"/>.</summary>
public sealed class NullRunPauseGate : IRunPauseGate
{
    public static NullRunPauseGate Instance { get; } = new();
    public bool IsRunPaused(Guid runId) => false;
    public IDisposable Subscribe(Action<Guid, bool> pauseHandler) => NullSubscription.Instance;

    private sealed class NullSubscription : IDisposable
    {
        public static NullSubscription Instance { get; } = new();
        public void Dispose() { }
    }
}
