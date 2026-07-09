namespace FileManager.Contracts.Settings;

/// <summary>Controls when the worker service is started and stopped relative to the UI, edited in the
/// UI and persisted in <see cref="GlobalSettings"/>.</summary>
public enum ServiceStartupMode
{
    /// <summary>The UI starts the service on open and gracefully stops it when the UI closes. If the
    /// service reports active jobs, the user is warned before the close proceeds. This is the default,
    /// and is deliberately the zero value so any initializer-bypassing path lands on the fail-safe
    /// mode (never on the side-effecting <see cref="RunOnStartup"/>).</summary>
    StartAndStopWithProgram,

    /// <summary>Register the service under the per-user HKCU Run key so it auto-starts at Windows
    /// login. The UI just connects to the already-running service and leaves it running on close.</summary>
    RunOnStartup,

    /// <summary>The UI starts the service on open (if it is not already running) and leaves it running
    /// after the UI closes — the historical behavior.</summary>
    StartOnProgramOpen,
}
