using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

public sealed class IpcClient : IAsyncDisposable
{
    public static Task<Result<IpcClient, string>> ConnectAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<Result<TResponse, IpcError>> RequestAsync<TResponse>(
        IpcRequest request, CancellationToken ct = default) where TResponse : IpcResponse =>
        throw new NotImplementedException();

    public IAsyncEnumerable<EngineEvent> SubscribeAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

public sealed record IpcError(string Code, string Message);
