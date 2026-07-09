using FileManager.Contracts.IPC;
using FileManager.Core.Settings;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetSettingsHandler(ISettingsProvider settings) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetSettings;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        IpcResponse response = new SettingsResponse { Settings = settings.Current };
        return Task.FromResult(response);
    }
}
