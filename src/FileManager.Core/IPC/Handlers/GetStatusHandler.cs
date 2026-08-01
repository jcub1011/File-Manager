using FileManager.Contracts.IPC;
using FileManager.Core.Jobs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetStatusHandler(IJobOrchestrator orchestrator, EngineStartupState startup)
    : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetStatus;

    /// <summary>Resolved once: it cannot change for the life of the process, and this handler answers
    /// the UI's 2 s poll.</summary>
    private static readonly string? ExecutablePath = Environment.ProcessPath;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        // The orchestrator owns engine state; where this process was launched from, and how it came
        // up, are the host's own business, so both are stamped here rather than threaded through the
        // engine. The startup warning rides the poll because the event that announces it is published
        // before any client can have subscribed.
        IpcResponse response = new StatusResponse
        {
            Status = orchestrator.GetStatus() with
            {
                ExecutablePath = ExecutablePath,
                StartupWarning = startup.Warning,
            },
        };
        return Task.FromResult(response);
    }
}
