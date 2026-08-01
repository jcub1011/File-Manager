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
using System.Collections.Concurrent;
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
    ILogger<RunProfileHandler> logger) : IIpcRequestHandler, IDisposable
{
    /// <summary>Cancels the detached folder enumerations this handler starts. The walk used to run under
    /// <see cref="CancellationToken.None"/> with no owner at all, so nothing in the process could stop it:
    /// closing the app (which in StartAndStopWithProgram mode shuts the service down) left it walking a
    /// large tree and holding ScanScheduler's LongRunning worker threads until the process died. The
    /// handler is a container singleton, so disposal — and therefore this cancel — happens as the host
    /// tears the graph down.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>The detached walks started so far, so <see cref="Dispose"/> can wait for them to observe
    /// the cancel before it disposes the source they are still reading the token from. Entries are
    /// removed as they finish, so a long-lived service does not accumulate one per manual run.</summary>
    private readonly ConcurrentDictionary<Task, byte> _walks = new();

    /// <summary>How long <see cref="Dispose"/> waits for the walks to unwind. A bound, not a promise:
    /// teardown must finish even if a walk is wedged on an unresponsive network path, and the process is
    /// exiting anyway. Matches the drain budget the orchestrator gets.</summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

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
            // Under the handler's own shutdown token, not CancellationToken.None: an unowned walk is one
            // nothing can stop. Tracked so Dispose can wait for it rather than pulling the token source
            // out from under a walk that is still reading it.
            TrackWalk(Task.Run(() => ScanAndEnqueue(profile, typed.Path, runId), _shutdown.Token));
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
        int skipped = 0;
        string? error = null;
        try
        {
            // The token reaches the walk itself, so a shutdown stops enumerating rather than only
            // stopping the Task from being scheduled.
            foreach (Result<Payload, EnumerationFault> scanned in
                scanner.Scan(profile, TriggerKind.ManualShell, folder, _shutdown.Token))
            {
                if (scanned.TryGetError(out EnumerationFault fault))
                {
                    if (fault.Severity == EnumerationSeverity.Fatal)
                    {
                        logger.LogWarning("Manual run of profile {ProfileId} aborted enumeration: {Message}", profile.Id, fault.Message);
                        error = fault.Message;
                        break;
                    }
                    // A subdirectory the walk could not open (SourceScanner downgrades that to a
                    // Warning so siblings still get walked). Counted, not just logged: the notice
                    // otherwise reads "Queued 812 file(s) from C:\..." over a partial tree.
                    skipped++;
                    logger.LogWarning("Manual run of profile {ProfileId} enumeration warning: {Message}", profile.Id, fault.Message);
                    continue;
                }
                scanned.TryGetValue(out Payload? payload);
                queue.Enqueue(payload!);
                queued++;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutdown cut the walk short. Reported as an error on the terminal event below with the
            // partial count — the same shape a fatal fault produces — rather than as a clean zero.
            logger.LogInformation(
                "Background enumeration for manual run of profile {ProfileId} stopped at shutdown after {Queued} file(s)",
                profile.Id, queued);
            error = "the service shut down before the folder had been fully enumerated";
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

            // Partiality the count alone cannot express. RunQueuedEvent.Error is reserved for a Fatal
            // fault (the walk stopped), so a tree that was walked but incompletely rides the warning
            // channel instead — otherwise the user approves a run whose OnSuccess may be
            // PermanentDelete believing the whole tree was covered.
            if (skipped > 0)
                eventBus.Publish(new EngineWarningEvent
                {
                    AtUtc = time.GetUtcNow(),
                    Message = $"{skipped} item(s) under {folder} could not be read and were not queued (see the service log for details).",
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing run-queued for profile {ProfileId} failed", profile.Id);
        }
    }

    /// <summary>Registers a detached walk and self-removes it on completion, so the set holds only what
    /// is actually still running. The continuation is inline and never faults — <see cref="ScanAndEnqueue"/>
    /// has its own catch-all — so there is nothing here to observe.</summary>
    private void TrackWalk(Task walk)
    {
        _walks[walk] = 0;
        _ = walk.ContinueWith(
            static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
            _walks, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Cancels the detached walks and WAITS for them before disposing the token source.
    /// <para>The wait is the point. Cancel returns as soon as it has run the registered callbacks, but a
    /// walk is still inside <c>scanner.Scan</c> reading <c>_shutdown.Token</c> — and a token whose source
    /// has been disposed throws <see cref="ObjectDisposedException"/> from
    /// <c>CreateLinkedTokenSource</c> and <c>Register</c>. Disposing straight after the cancel therefore
    /// turned an orderly shutdown into a thrown exception on a background thread, swallowed by
    /// ScanAndEnqueue's catch-all and reported to the client as an unexplained failure instead of "the
    /// service shut down".</para></summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            // ToArray: the walks remove themselves as they finish, and WaitAll needs a stable set.
            Task[] pending = [.. _walks.Keys];
            if (pending.Length > 0 && !Task.WaitAll(pending, DisposeDrainTimeout))
            {
                logger.LogWarning(
                    "{Count} manual-run enumeration(s) did not stop within {Timeout}; disposing anyway",
                    pending.Length, DisposeDrainTimeout);
            }
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a faulted walk must not stop teardown. WaitAll wraps
            // whatever the tasks threw, and every one of them already logged it.
            logger.LogWarning(ex, "Waiting for manual-run enumerations to stop failed");
        }
        _shutdown.Dispose();
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
