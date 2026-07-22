using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.DryRun;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

public sealed class DryRunHandler(ILogger<DryRunHandler> logger, IDryRunEngine engine, IProfileCatalog catalog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.DryRun;

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (DryRunRequest)request;
        // An inline draft (unsaved edits) is previewed directly; otherwise resolve the persisted
        // catalog. PROFILE_NOT_FOUND is only reachable on the non-inline path. Accepting a
        // client-supplied profile grants no new authority: the dry run is read-only (I-DRYRUN-RO)
        // and the service runs at the same trust level as the local UI over the local IPC channel.
        var profile = typed.InlineProfile ?? catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
        {
            logger.LogDebug("DryRun: requested profile {ProfileId} not found", typed.ProfileId);
            return new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
        }

        var simulated = await engine.SimulateAsync(profile, typed.ScopePath, ct).ConfigureAwait(false);

        if (simulated.IsCanceled)
            // Server-side cancellation only happens on shutdown; the connection is tearing down,
            // so this response is best-effort.
            return new ErrorResponse { Code = "CANCELED", Message = "the dry run was canceled" };
        if (simulated.TryGetError(out string? error))
            return new ErrorResponse { Code = "DRY_RUN_FAILED", Message = error };
        simulated.TryGetValue(out DryRunReport? report);
        return new DryRunResponse { Report = report! };
    }
}
