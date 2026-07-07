using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Always answers with the validation issues; whether the profile was written is
/// derivable from the severities plus the request's AcknowledgeWarnings flag (the same rule
/// IProfileStore.Save applies: any Error blocks, any BlockingWarning blocks unless
/// acknowledged). An ErrorResponse means an I/O fault, not a validation rejection.</summary>
public sealed class SaveProfileHandler(
    ILogger<SaveProfileHandler> logger, IProfileStore store, IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.SaveProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (SaveProfileRequest)request;
        var saved = store.Save(typed.Profile, typed.AcknowledgeWarnings);
        if (saved.TryGetError(out string? ioError))
        {
            IpcResponse failure = new ErrorResponse { Code = "PROFILE_SAVE_FAILED", Message = ioError };
            return Task.FromResult(failure);
        }
        saved.TryGetValue(out IReadOnlyList<ValidationIssue>? issues);

        Result reload = catalog.Reload();
        if (reload.TryGetError(out string? reloadError))
            logger.LogWarning("Catalog reload after save failed: {Error}", reloadError);

        IpcResponse response = new ValidationResponse { Issues = issues! };
        return Task.FromResult(response);
    }
}
