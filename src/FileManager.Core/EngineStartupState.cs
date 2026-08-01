using System.Threading;

namespace FileManager.Core;

/// <summary>Facts about how this service process came up that a client has to be able to learn after
/// the fact. A container singleton written once by the host during startup and read by
/// <c>GetStatusHandler</c> on every poll.
/// <para>It exists because an event cannot carry this: the host publishes its startup warning
/// microseconds after the IPC server opens, and a UI that launched the service has not finished
/// subscribing by then, so the event is dropped in exactly the flow that matters. Polled state has no
/// such race.</para>
/// <para>Deliberately NOT folded into <c>JobOrchestrator.LastError</c>: that is per-job health, is
/// cleared by the next successful job, and is reset by <c>Start()</c> — which the host calls AFTER the
/// point where the warning is known.</para></summary>
public sealed class EngineStartupState
{
    private string? _warning;

    /// <summary>A problem that left the engine running but degraded — today, profiles that could not be
    /// loaded. Null when startup was clean. Volatile because it is written on the host's startup thread
    /// and read on IPC handler threads.</summary>
    public string? Warning
    {
        get => Volatile.Read(ref _warning);
        set => Volatile.Write(ref _warning, value);
    }
}
