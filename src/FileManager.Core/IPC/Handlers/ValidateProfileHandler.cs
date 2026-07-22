using FileManager.Contracts.IPC;
using FileManager.Core.Profiles;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class ValidateProfileHandler(IProfileValidator validator, IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.ValidateProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (ValidateProfileRequest)request;
        var otherActive = catalog.Active.Where(p => p.Id != typed.Profile.Id).ToList();
        IpcResponse response = new ValidationResponse
        {
            Issues = validator.Validate(typed.Profile, otherActive),
        };
        return Task.FromResult(response);
    }
}
