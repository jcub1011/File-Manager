using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Profiles;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Handles run-profile (§4.9, spec §3.2 / flow §6.3): a manual invocation that bypasses the
/// picker. A single file is enqueued directly; a folder is enumerated recursively (honoring MaxDepth)
/// on a background task so the reply stays prompt (§8 rule 5). Payloads carry
/// <see cref="TriggerKind.ManualShell"/>; while paused they queue (the trigger queue's gate). The
/// handler enqueues and returns — it never blocks on job completion.</summary>
public sealed class RunProfileHandler(
    IProfileCatalog catalog,
    ITriggerQueue queue,
    ISourceScanner scanner,
    TimeProvider time,
    ILogger<RunProfileHandler> logger) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.RunProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (RunProfileRequest)request;
        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
        {
            IpcResponse notFound = new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
            return Task.FromResult(notFound);
        }

        if (File.Exists(typed.Path))
        {
            string sourceRoot = ResolveSourceRoot(profile, typed.Path)
                ?? Path.GetDirectoryName(typed.Path) ?? typed.Path;
            queue.Enqueue(new Payload(profile.Id, typed.Path, sourceRoot, TriggerKind.ManualShell, time.GetUtcNow()));
            logger.LogInformation("Enqueued manual run of profile {ProfileId} for file {Path}", profile.Id, typed.Path);
        }
        else if (Directory.Exists(typed.Path))
        {
            // Enumerate off the IPC thread so the reply is immediate; captures its own copy of profile.
            _ = Task.Run(() => ScanAndEnqueue(profile, typed.Path), CancellationToken.None);
            logger.LogInformation("Started background enumeration for manual run of profile {ProfileId} at folder {Path}", profile.Id, typed.Path);
        }
        else
        {
            IpcResponse notFound = new ErrorResponse { Code = "PATH_NOT_FOUND", Message = $"path does not exist: {typed.Path}" };
            return Task.FromResult(notFound);
        }

        IpcResponse ok = new OkResponse();
        return Task.FromResult(ok);
    }

    private void ScanAndEnqueue(Profile profile, string folder)
    {
        try
        {
            foreach (Result<Payload, EnumerationFault> scanned in scanner.Scan(profile, TriggerKind.ManualShell, folder))
            {
                if (scanned.TryGetError(out EnumerationFault fault))
                {
                    if (fault.Severity == EnumerationSeverity.Fatal)
                    {
                        logger.LogWarning("Manual run of profile {ProfileId} aborted enumeration: {Message}", profile.Id, fault.Message);
                        return;
                    }
                    logger.LogWarning("Manual run of profile {ProfileId} enumeration warning: {Message}", profile.Id, fault.Message);
                    continue;
                }
                scanned.TryGetValue(out Payload? payload);
                queue.Enqueue(payload!);
            }
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log at this task boundary: an unexpected enumeration throw must
            // not vanish silently.
            logger.LogError(ex, "Background enumeration for manual run of profile {ProfileId} failed", profile.Id);
        }
    }

    private static string? ResolveSourceRoot(Profile profile, string filePath)
    {
        if (!NormalizedPath.Create(filePath).TryGetValue(out NormalizedPath target))
            return null;
        foreach (SourceConfig source in profile.Sources)
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root) && (target == root || target.IsUnder(root)))
                return root.Value;
        return null;
    }
}
