using FileManager.Contracts.IPC;
using FileManager.Core.Jobs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetStatusHandler(IJobOrchestrator orchestrator) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetStatus;

    /// <summary>Resolved once: it cannot change for the life of the process, and this handler answers
    /// the UI's 2 s poll.</summary>
    private static readonly string? ExecutablePath = Environment.ProcessPath;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        // The orchestrator owns engine state; where this process was launched from is the host's own
        // business, so it is stamped here rather than threaded through the engine.
        IpcResponse response = new StatusResponse
        {
            Status = orchestrator.GetStatus() with { ExecutablePath = ExecutablePath },
        };
        return Task.FromResult(response);
    }
}
