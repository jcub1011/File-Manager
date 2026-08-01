using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Handles set-paused (§4.9, spec §3.2.4): toggles the persisted global pause flag. The
/// resulting <c>pause-changed</c> event is published via the EngineHost bridge on the pause-state
/// subscription, not here.</summary>
public sealed class SetPausedHandler(IPauseStateService pauseState, ILogger<SetPausedHandler> logger) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.SetPaused;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (SetPausedRequest)request;
        Result result = pauseState.SetPaused(typed.Paused);
        if (result.TryGetError(out string? error))
        {
            logger.LogWarning("SetPaused({Paused}) failed: {Error}", typed.Paused, error);
            IpcResponse failure = new ErrorResponse { Code = "SET_PAUSED_FAILED", Message = error };
            return Task.FromResult(failure);
        }
        IpcResponse ok = new OkResponse();
        return Task.FromResult(ok);
    }
}
