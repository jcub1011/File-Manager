using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Profiles;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class GetProfileHandler(IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetProfileRequest)request;
        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        IpcResponse response = profile is null
            ? new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" }
            : new ProfileResponse { Profile = profile };
        return Task.FromResult(response);
    }
}
