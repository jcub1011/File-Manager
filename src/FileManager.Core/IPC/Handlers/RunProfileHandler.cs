using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
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
/// picker. A single file is enqueued directly, so the reply's QueuedCount is exact; a folder is
/// enumerated recursively (honoring MaxDepth) on a background task so the reply stays prompt
/// (§8 rule 5), and its final count — including zero for "nothing matched" — arrives later as a
/// <see cref="RunQueuedEvent"/>. Payloads carry <see cref="TriggerKind.ManualShell"/>; while paused
/// they queue (the trigger queue's gate). The handler enqueues and returns — it never blocks on job
/// completion. An inactive profile — and a path under no Source of the profile — is refused outright so
/// the caller learns why nothing happened, rather than having the payload vanish into the
/// orchestrator's silent drop (or, worse, run against a file the profile does not cover).</summary>
public sealed class RunProfileHandler(
    IProfileCatalog catalog,
    ITriggerQueue queue,
    ISourceScanner scanner,
    IEngineEventBus eventBus,
    TimeProvider time,
    ILogger<RunProfileHandler> logger) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.RunProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (RunProfileRequest)request;
        // catalog.All, not Active, so "no such profile" and "inactive" stay distinguishable.
        Profile? profile = catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
        {
            IpcResponse notFound = new ErrorResponse { Code = "PROFILE_NOT_FOUND", Message = $"no profile with id {typed.ProfileId}" };
            return Task.FromResult(notFound);
        }
        if (!profile.Active)
        {
            IpcResponse inactive = new ErrorResponse
            {
                Code = "PROFILE_INACTIVE",
                Message = $"profile \"{profile.Name}\" is inactive; activate it before running it",
            };
            return Task.FromResult(inactive);
        }

        // One id per accepted invocation, echoed on the RunQueuedEvent so the requester can pick its own
        // run's outcome out of the broadcast.
        Guid runId = Guid.NewGuid();

        IpcResponse response;
        if (File.Exists(typed.Path))
        {
            // Containment is NOT optional here. The folder branch gets it from the scanner, which
            // refuses an out-of-scope scope path with a Fatal fault; a file branch that fell back to
            // the file's own directory would run the profile — and then its OnSuccess disposition, up
            // to PermanentDelete — against a file the profile was never configured to touch.
            string? sourceRoot = ResolveSourceRoot(profile, typed.Path);
            if (sourceRoot is null)
            {
                IpcResponse outOfScope = new ErrorResponse
                {
                    Code = "PATH_OUT_OF_SCOPE",
                    Message = $"path \"{typed.Path}\" is not under any Source of the profile",
                };
                return Task.FromResult(outOfScope);
            }
            queue.Enqueue(new Payload(profile.Id, typed.Path, sourceRoot, TriggerKind.ManualShell, time.GetUtcNow()));
            logger.LogInformation("Enqueued manual run of profile {ProfileId} for file {Path}", profile.Id, typed.Path);
            response = new RunProfileResponse { QueuedCount = 1, Scanning = false, RunId = runId };
        }
        else if (Directory.Exists(typed.Path))
        {
            // Enumerate off the IPC thread so the reply is immediate; captures its own copy of profile.
            _ = Task.Run(() => ScanAndEnqueue(profile, typed.Path, runId), CancellationToken.None);
            logger.LogInformation("Started background enumeration for manual run of profile {ProfileId} at folder {Path}", profile.Id, typed.Path);
            response = new RunProfileResponse { QueuedCount = 0, Scanning = true, RunId = runId };
        }
        else
        {
            response = new ErrorResponse { Code = "PATH_NOT_FOUND", Message = $"path does not exist: {typed.Path}" };
        }

        return Task.FromResult(response);
    }

    private void ScanAndEnqueue(Profile profile, string folder, Guid runId)
    {
        int queued = 0;
        string? error = null;
        try
        {
            foreach (Result<Payload, EnumerationFault> scanned in scanner.Scan(profile, TriggerKind.ManualShell, folder))
            {
                if (scanned.TryGetError(out EnumerationFault fault))
                {
                    if (fault.Severity == EnumerationSeverity.Fatal)
                    {
                        logger.LogWarning("Manual run of profile {ProfileId} aborted enumeration: {Message}", profile.Id, fault.Message);
                        error = fault.Message;
                        break;
                    }
                    logger.LogWarning("Manual run of profile {ProfileId} enumeration warning: {Message}", profile.Id, fault.Message);
                    continue;
                }
                scanned.TryGetValue(out Payload? payload);
                queue.Enqueue(payload!);
                queued++;
            }
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log at this task boundary: an unexpected enumeration throw must
            // not vanish silently.
            logger.LogError(ex, "Background enumeration for manual run of profile {ProfileId} failed", profile.Id);
            error = ex.Message;
        }

        // Always terminate the run, even on a fault or a zero-match scan: this event is the ONLY way
        // a caller that got Scanning=true learns the outcome. Guarded so a throwing bus subscriber
        // cannot kill this task silently.
        try
        {
            eventBus.Publish(new RunQueuedEvent
            {
                AtUtc = time.GetUtcNow(),
                ProfileId = profile.Id,
                ScopePath = folder,
                QueuedCount = queued,
                Error = error,
                RunId = runId,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing run-queued for profile {ProfileId} failed", profile.Id);
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
