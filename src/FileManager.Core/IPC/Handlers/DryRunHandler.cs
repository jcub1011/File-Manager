using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.DryRun;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;
using System;
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
        if (catalog.All.All(p => p.Id != typed.ProfileId))
        {
            logger.LogDebug("DryRun: requested profile {ProfileId} not found", typed.ProfileId);
            return new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
        }

        var simulated = await engine.SimulateAsync(typed.ProfileId, typed.ScopePath, ct).ConfigureAwait(false);

        // A large dry run leaves tens of MB of committed-but-free heap behind; the default GC
        // decommits it lazily, so the service's working set stays inflated while idle. Aggressive
        // mode compacts and returns the memory to the OS. Fire-and-forget so the reply isn't
        // delayed behind a full blocking collection.
        _ = Task.Run(() =>
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Post-dry-run aggressive GC failed");
            }
        }, CancellationToken.None);

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
