using FileManager.Contracts.IPC;
using FileManager.Core.Profiles;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetStatusHandler(IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetStatus;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        // Pause and job execution are out of this slice; the snapshot reflects that honestly.
        IpcResponse response = new StatusResponse
        {
            Status = new EngineStatusSnapshot(
                Paused: false,
                ActiveProfiles: catalog.Active.Count,
                JobsInFlight: 0,
                QueuedPayloads: 0,
                LastError: null),
        };
        return Task.FromResult(response);
    }
}
