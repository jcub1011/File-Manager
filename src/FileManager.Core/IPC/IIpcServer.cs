using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using System.Collections.Generic;
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

/// <summary>A handler whose one request produces a STREAM of response frames on the connection
/// (e.g. a paged dry-run report), terminated when the sequence ends. The server writes each yielded
/// response as its own frame, applying the same oversize guard as a single response. Requests stay
/// strictly sequential per connection: the client consumes the whole stream before its next request.
/// <see cref="IIpcRequestHandler.HandleAsync"/> is never called on a streaming handler (the server
/// routes to <see cref="HandleStreamAsync"/>); implementations throw from it.</summary>
public interface IIpcStreamingRequestHandler : IIpcRequestHandler
{
    IAsyncEnumerable<IpcResponse> HandleStreamAsync(IpcRequest request, CancellationToken ct = default);
}
