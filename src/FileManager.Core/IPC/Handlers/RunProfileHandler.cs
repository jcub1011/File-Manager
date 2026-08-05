using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Profiles;
using FileManager.Core.Runs;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Handles run-profile (§4.9, spec §3.2 / flow §6.3): a manual invocation that bypasses the
/// picker.
///
/// <para>The handler accepts the run and returns; it does no scanning of its own. A run's first phase
/// is to plan — scan and evaluate the whole source set into a frozen work list — which cannot happen
/// inside an IPC round trip (§8 rule 5), so <see cref="IRunCoordinator"/> owns it on a background task
/// and the outcome arrives as a <c>run-planned</c> event. Nothing is copied or deleted until the run is
/// approved.</para>
///
/// <para><b>Path is optional.</b> Null means the whole profile — every Source — and is the normal case.
/// A narrowed run is still supported (a shell invocation on one folder), but it must be contained: a
/// path under no Source of the profile is refused outright rather than silently running the profile's
/// disposition, up to PermanentDelete, against a file the profile was never configured to
/// touch.</para>
///
/// <para><b>InlineProfile.</b> A request may carry an unsaved draft to plan instead of a persisted
/// profile, exactly as <c>dry-run-stream</c> can. This is what lets the GUI's Preview tab BE this run's
/// planning phase: the user previews what is on screen, and approving executes that same frozen plan.
/// The gates below — active, has sources, scope containment — apply to whichever profile was
/// resolved.</para>
///
/// <para>The §4.1 validation gates do NOT live here, deliberately. A draft that would fail them can
/// still be PLANNED, because planning reads and writes nothing outside the run's own snapshot; it is
/// <c>approve-run</c> that refuses to execute one, and that carries the acknowledgment. Gating here
/// instead would refuse a read-only preview — the one thing a user needs in order to see WHY the
/// profile is dangerous.</para></summary>
public sealed class RunProfileHandler(
    IProfileCatalog catalog,
    IRunCoordinator runs,
    ILogger<RunProfileHandler> logger) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.RunProfile;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (RunProfileRequest)request;
        // A supplied draft IS the profile — no catalog lookup, so a never-saved profile is runnable.
        // Otherwise catalog.All, not Active, so "no such profile" and "inactive" stay distinguishable.
        Profile? profile = typed.InlineProfile ?? catalog.All.FirstOrDefault(p => p.Id == typed.ProfileId);
        if (profile is null)
            return Reply(new ErrorResponse
            {
                Code = "PROFILE_NOT_FOUND",
                Message = $"no profile with id {typed.ProfileId}",
            });
        if (!profile.Active)
            return Reply(new ErrorResponse
            {
                Code = "PROFILE_INACTIVE",
                Message = $"profile \"{profile.Name}\" is inactive; activate it before running it",
            });
        if (profile.Sources.Count == 0)
            return Reply(new ErrorResponse
            {
                Code = "PROFILE_NO_SOURCES",
                Message = $"profile \"{profile.Name}\" has no sources to run",
            });

        string? scope = typed.Path;
        if (scope is not null)
        {
            if (!File.Exists(scope) && !Directory.Exists(scope))
                return Reply(new ErrorResponse { Code = "PATH_NOT_FOUND", Message = $"path does not exist: {scope}" });
            // Containment is NOT optional. Without it a narrowed run would apply the profile — and then
            // its OnSuccess disposition — to a file outside every configured Source.
            if (!IsUnderAnySource(profile, scope))
                return Reply(new ErrorResponse
                {
                    Code = "PATH_OUT_OF_SCOPE",
                    Message = $"path \"{scope}\" is not under any Source of the profile",
                });
        }

        Result<RunHandle, string> started = runs.Begin(profile, scope);
        if (started.TryGetError(out string? error))
            return Reply(new ErrorResponse { Code = "RUN_NOT_STARTED", Message = error });
        started.TryGetValue(out RunHandle? handle);

        logger.LogInformation(
            "Accepted run {RunId} of profile {ProfileId} ({Scope})",
            handle!.RunId, profile.Id, scope ?? "whole profile");
        return Reply(new RunProfileResponse { RunId = handle.RunId });
    }

    private static Task<IpcResponse> Reply(IpcResponse response) => Task.FromResult(response);

    private static bool IsUnderAnySource(Profile profile, string path)
    {
        if (!NormalizedPath.Create(path).TryGetValue(out NormalizedPath target))
            return false;
        foreach (SourceConfig source in profile.Sources)
            if (NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root)
                && (target == root || target.IsUnder(root)))
                return true;
        return false;
    }
}

/// <summary>Approves or declines a planned run. Declining changes nothing on disk — at that point the
/// run has only produced a work list.</summary>
public sealed class ApproveRunHandler(IRunCoordinator runs, ILogger<ApproveRunHandler> logger) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.ApproveRun;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (ApproveRunRequest)request;
        Result result = runs.Approve(typed.RunId, typed.Approve, typed.AcknowledgeWarnings);
        if (result.TryGetError(out string? error))
        {
            logger.LogInformation("Approve({Approve}) for run {RunId} refused: {Error}", typed.Approve, typed.RunId, error);
            IpcResponse failure = new ErrorResponse { Code = "RUN_NOT_APPROVABLE", Message = error };
            return Task.FromResult(failure);
        }
        IpcResponse ok = new OkResponse();
        return Task.FromResult(ok);
    }
}

/// <summary>Cancels a run. Work not yet started is dropped; work in flight finishes (I-ATOMIC-JOB).</summary>
public sealed class CancelRunHandler(IRunCoordinator runs) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.CancelRun;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (CancelRunRequest)request;
        Result result = runs.Cancel(typed.RunId);
        IpcResponse response = result.TryGetError(out string? error)
            ? new ErrorResponse { Code = "RUN_NOT_FOUND", Message = error }
            : new OkResponse();
        return Task.FromResult(response);
    }
}

/// <summary>Handles get-runs: every run the coordinator still holds, newest first.
///
/// <para>Answered entirely from in-memory state, like get-status and unlike get-recent-jobs' file reads —
/// so it is cheap enough for a queue window to re-seed from on every reconnect, which it must, because the
/// event stream it otherwise follows is bounded and drop-oldest.</para>
///
/// <para>An empty list is a legitimate answer (an idle engine), never an error — the same contract
/// get-recent-jobs documents for its wiped-on-restart ring.</para></summary>
public sealed class GetRunsHandler(IRunCoordinator runs) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetRuns;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        IpcResponse response = new RunsResponse { Runs = runs.ListRuns() };
        return Task.FromResult(response);
    }
}

/// <summary>Handles set-run-paused: holds or releases ONE run, independently of the global engine pause.
///
/// <para>RUN_NOT_FOUND covers both an unknown id and a run that has already closed. Both are normal races
/// for a queue window whose rows come from a lossy stream, so the code is deliberately the same one
/// <c>cancel-run</c> uses rather than a new one a client would have to learn.</para></summary>
public sealed class SetRunPausedHandler(IRunCoordinator runs) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.SetRunPaused;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (SetRunPausedRequest)request;
        Result result = runs.SetPaused(typed.RunId, typed.Paused);
        IpcResponse response = result.TryGetError(out string? error)
            ? new ErrorResponse { Code = "RUN_NOT_FOUND", Message = error }
            : new OkResponse();
        return Task.FromResult(response);
    }
}
