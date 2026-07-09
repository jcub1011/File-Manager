using FileManager.Contracts.IPC;
using FileManager.Core.IPC;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Service;

/// <summary>Handles a <see cref="ShutdownRequest"/> (sent by the UI on close in
/// StartAndStopWithProgram mode). Acknowledges immediately, then stops the host on a short delay so
/// the OkResponse frame flushes to the client before the IPC server tears down. Lives in the Service
/// project (the composition root) because stopping the host is a hosting concern — Core stays
/// hosting-free.</summary>
internal sealed class ShutdownHandler(ILogger<ShutdownHandler> logger, IHostApplicationLifetime lifetime)
    : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.Shutdown;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        logger.LogInformation("Shutdown requested over IPC; stopping the service");

        // Defer the stop so the OkResponse below reaches the client before the pipe closes.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
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
