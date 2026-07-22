using FileManager.Contracts.IPC;
using FileManager.Core.IPC;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Service;

/// <summary>Handles a <see cref="ShutdownRequest"/> (sent by the UI on close in
/// StartAndStopWithProgram mode). Acknowledges immediately, then stops the host after a short delay so
/// the OkResponse frame flushes to the client before the IPC server tears down. Lives in the Service
/// project (the composition root) because stopping the host is a hosting concern — Core stays
/// hosting-free.</summary>
internal sealed class ShutdownHandler(ILogger<ShutdownHandler> logger, IHostApplicationLifetime lifetime)
    : IIpcRequestHandler
{
    /// <summary>Best-effort window for the OkResponse to flush before the host stops. There is no
    /// post-response flush hook to await, so this is a heuristic, not a guarantee — but the caller
    /// (<c>MainWindowViewModel.RequestCloseAsync</c>) treats shutdown-on-close as best-effort and
    /// closes regardless of whether the ack arrives, so a lost ack is harmless.</summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromMilliseconds(250);

    public string RequestType => IpcRequestTypes.Shutdown;

    // The cancellation token (the server-lifetime token the IPC server threads through every handler)
    // is intentionally ignored. The synchronous body has nothing cancellable, and the deferred stop
    // must NOT observe it: this handler's whole job is to stop the host, so cancelling the delay would
    // only abort the very shutdown we were asked to perform. Task.Delay is therefore called without a
    // token on purpose.
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        logger.LogInformation("Shutdown requested over IPC; stopping the service");

        // Defer the stop so the OkResponse below reaches the client before the pipe closes. No token
        // passed to Task.Delay — see the note above; the shutdown must proceed regardless of ct.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StopDelay).ConfigureAwait(false);
                lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                // Last resort: a deferred-stop failure must be traceable, not silently swallowed.
                logger.LogError(ex, "Deferred shutdown failed after acknowledging the shutdown request");
            }
        });

        IpcResponse response = new OkResponse();
        return Task.FromResult(response);
    }
}
