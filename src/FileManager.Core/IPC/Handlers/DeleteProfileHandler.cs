using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class DeleteProfileHandler(
    ILogger<DeleteProfileHandler> logger, IProfileStore store, IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.DeleteProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (DeleteProfileRequest)request;
        Result deleted = store.Delete(typed.ProfileId);
        if (deleted.TryGetError(out string? error))
        {
            IpcResponse failure = new ErrorResponse { Code = "PROFILE_DELETE_FAILED", Message = error };
            return Task.FromResult(failure);
        }

        if (CatalogReloadGuard.Check(catalog, logger, "PROFILE_DELETE_FAILED", "deleted") is { } reloadFailure)
            return Task.FromResult<IpcResponse>(reloadFailure);

        IpcResponse response = new OkResponse();
        return Task.FromResult(response);
    }
}
