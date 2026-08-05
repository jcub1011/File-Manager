using FileManager.Core.Observability;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;

namespace FileManager.Core.Watching;

/// <summary>The per-run pause flags, as a standalone registry.
///
/// <para><b>Why this is not just a property on the run coordinator.</b> The coordinator depends on
/// <see cref="ITriggerQueue"/> (it enqueues a run's copies) and the queue must read pause state on every
/// dequeue — so a coordinator that also answered <see cref="IRunPauseGate"/> would close a dependency
/// cycle the container refuses to resolve. Pulling the one piece of shared state into a registry both
/// depend on breaks it, which is the same shape <see cref="PauseStateService"/>,
/// <c>PathLockRegistry</c> and <c>SelfWriteSuppressionRegistry</c> already have: a small, single-purpose
/// piece of cross-thread state that several services coordinate through rather than through each other
/// (§8 rule 1).</para>
///
/// <para>The coordinator remains the only legitimate WRITER — it is what validates that the run exists and
/// has not closed, and it keeps the barrier-clock accounting a pause implies. This type holds the flag and
/// fans out the transition.</para></summary>
public sealed class RunPauseRegistry(ILogger<RunPauseRegistry> logger) : IRunPauseGate
{
    // Only paused runs are present, so the ordinary state is an empty dictionary and the hot-path lookup
    // on an unpaused engine is a single miss. Entries are removed on resume and by Forget.
    private readonly ConcurrentDictionary<Guid, bool> _paused = new();
    private readonly SubscriberList<(Guid RunId, bool Paused)> _subscribers =
        new(logger, "run pause subscriber");

    public bool IsRunPaused(Guid runId) => _paused.ContainsKey(runId);

    public IDisposable Subscribe(Action<Guid, bool> pauseHandler)
    {
        ArgumentNullException.ThrowIfNull(pauseHandler);
        return _subscribers.Subscribe(change => pauseHandler(change.RunId, change.Paused));
    }

    /// <summary>Sets a run's pause flag and notifies, when it actually changed. Returns whether it did, so
    /// the caller can keep a no-op idempotent rather than announcing a transition that never happened.</summary>
    public bool Set(Guid runId, bool paused)
    {
        bool changed = paused ? _paused.TryAdd(runId, true) : _paused.TryRemove(runId, out _);
        if (changed)
            _subscribers.Notify((runId, paused));
        return changed;
    }

    /// <summary>Drops a run's entry without notifying — for a run that has closed.
    /// <para>Notifying would be wrong as well as pointless: the trigger queue reads a resume as "re-check
    /// the gate, there may be work to serve", and a closed run has none. Not notifying also means a run
    /// cancelled while paused does not produce a phantom resume.</para></summary>
    public void Forget(Guid runId) => _paused.TryRemove(runId, out _);
}
