using FileManager.Contracts.IPC;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetStatusHandler(IJobOrchestrator orchestrator) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetStatus;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        IpcResponse response = new StatusResponse { Status = orchestrator.GetStatus() };
        return Task.FromResult(response);
    }
}
