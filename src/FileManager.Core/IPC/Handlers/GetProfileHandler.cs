using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetProfileHandler(ILogger<GetProfileHandler> logger, IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetProfileRequest)request;
        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
        {
            logger.LogDebug("GetProfile: requested profile {ProfileId} not found", typed.ProfileId);
            IpcResponse notFound = new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
            return Task.FromResult(notFound);
        }
        IpcResponse response = new ProfileResponse { Profile = profile };
        return Task.FromResult(response);
    }
}
