using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class UpdateSettingsHandler(ILogger<UpdateSettingsHandler> logger, ISettingsProvider settings)
    : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.UpdateSettings;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (UpdateSettingsRequest)request;
        var result = settings.Update(typed.Settings);
        if (result.TryGetError(out string? error))
        {
            logger.LogWarning("UpdateSettings failed: {Error}", error);
            IpcResponse failure = new ErrorResponse { Code = "SETTINGS_SAVE_FAILED", Message = error };
            return Task.FromResult(failure);
        }
        result.TryGetValue(out GlobalSettings? saved);
        IpcResponse response = new SettingsResponse { Settings = saved! };
        return Task.FromResult(response);
    }
}
