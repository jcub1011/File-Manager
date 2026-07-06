using FileManager.Contracts.Primitives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

public static class ServiceLauncher
{
    /// <summary>Connect; on failure start FileManager.Service.exe and retry (§3.3).</summary>
    public static Task<Result<IpcClient, string>> ConnectOrStartAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
}
