using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC;

public interface IIpcServer
{
    Result Start();
    Task StopAsync(CancellationToken ct = default);
    void Broadcast(EngineEvent evt);      // fans out to all subscribed connections
}

/// <summary>One handler per request type. The Service host registers a dispatch table
/// (Dictionary&lt;string, IIpcRequestHandler&gt;) — no reflection-based discovery.</summary>
public interface IIpcRequestHandler
{
    string RequestType { get; }
    Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default);
}
