using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>Whether the profile was written, plus every validation issue the save produced.
/// Saved is false when an Error is present, or a BlockingWarning was not acknowledged.</summary>
public sealed record SaveOutcome(bool Saved, IReadOnlyList<ValidationIssue> Issues);

/// <summary>The UI's seam to the service: typed methods over the IPC client so viewmodels are
/// testable against a fake and never touch transport concerns.</summary>
public interface IIpcGateway
{
    Task<Result<EngineStatusSnapshot, IpcError>> GetStatusAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<ProfileSummary>, IpcError>> ListProfilesAsync(CancellationToken ct = default);
    Task<Result<Profile, IpcError>> GetProfileAsync(Guid profileId, CancellationToken ct = default);
    Task<Result<SaveOutcome, IpcError>> SaveProfileAsync(Profile profile, bool acknowledgeWarnings, CancellationToken ct = default);
    Task<Result<bool, IpcError>> DeleteProfileAsync(Guid profileId, CancellationToken ct = default);
    // No standalone dry-run method: the window's preview IS a run's planning phase, streamed back with
    // GetRunPlanStreamAsync below. A second way to produce the same rows — one whose result no run would
    // ever execute — is the thing this design exists to remove. The dry-run-stream request itself remains
    // a service capability for non-GUI clients.
    Task<Result<GlobalSettings, IpcError>> GetSettingsAsync(CancellationToken ct = default);
    Task<Result<GlobalSettings, IpcError>> SaveSettingsAsync(GlobalSettings settings, CancellationToken ct = default);
    /// <summary>Relocates the profiles storage directory, optionally moving the existing files.
    /// Returns the persisted settings (their <see cref="GlobalSettings.ProfilesDirectory"/> reflects
    /// the new, normalized location) plus the moved/skipped file outcome — skipped files mean a
    /// same-name copy already at the destination won, which the caller must surface.</summary>
    Task<Result<RelocateProfilesResponse, IpcError>> RelocateProfilesAsync(
        string newDirectory, bool moveExisting, CancellationToken ct = default);
    /// <summary>Asks the service to shut itself down (StartAndStopWithProgram mode on UI close, and
    /// when the user points the service executable path somewhere else).</summary>
    Task<Result<bool, IpcError>> ShutdownServiceAsync(CancellationToken ct = default);

    /// <summary>Forgets the current connection so the next request reconnects from scratch. Needed
    /// after the service executable path changes: the cached connection points at whatever was running
    /// before, so without this the UI keeps talking to the old service and the new setting looks like
    /// it did nothing.</summary>
    /// <param name="allowStart">True also clears the start cooldown, so the next request may spawn the
    /// configured executable. False arms the cooldown instead, leaving a plain reconnect — for a caller
    /// that resets repeatedly in a loop and must not turn each round into another process launch.</param>
    Task ResetConnectionAsync(bool allowStart = true);

    /// <summary>Manually invokes a profile against one file or folder (a folder is walked recursively,
    /// honoring MaxDepth). The service enqueues and answers immediately, so the response means
    /// "accepted", never "finished": a single file yields an exact <c>QueuedCount</c>, while a folder
    /// yields <c>Scanning = true</c> and reports its final count later as a <see cref="RunQueuedEvent"/>.
    /// PROFILE_NOT_FOUND / PROFILE_INACTIVE / PATH_NOT_FOUND surface as an <see cref="IpcError"/>.</summary>
    /// <summary>Starts a run. <paramref name="path"/> null means the whole profile, which is what the
    /// window sends and what Mirror requires. The run PLANS first and touches nothing until
    /// <see cref="ApproveRunAsync"/>; the plan's counts arrive as a <c>run-planned</c> event.
    /// <para>This planning phase IS the window's preview: when <paramref name="draft"/> is supplied the
    /// run plans that in-memory profile (unsaved edits) instead of resolving <paramref name="profileId"/>
    /// against the persisted catalog, so what the user sees and what they approve are one work list. The
    /// draft is frozen into the run's snapshot and the copies execute against it, not the catalog.</para></summary>
    Task<Result<RunProfileResponse, IpcError>> RunProfileAsync(
        Guid profileId, string? path = null, Profile? draft = null, CancellationToken ct = default);

    /// <summary>Streams a pending run's frozen plan — the rows it will execute if approved — folding each
    /// chunk into <paramref name="sink"/> as it arrives and returning only the run-level facts from the
    /// terminator.
    /// <para>It streams rather than returning a <c>DryRunReport</c> for a memory reason, not a stylistic
    /// one: assembling the report meant the whole plan was live in the client at the same time as the rows
    /// being projected out of it, which is most of the UI's peak at the 500k cap. The caller's sink
    /// (<c>DryRunRowStore</c>) folds each chunk into its columns and drops it.</para>
    /// <para>The service answers with the same frames a preview streams, so the preview's sink
    /// (<c>DryRunRowStore</c>) and renderer are reused unchanged. Rows are read back from the snapshot
    /// rather than re-planned: a re-scan would produce a DIFFERENT list from the one the run will
    /// execute, which would defeat the point of showing it.</para>
    /// <para>RUN_NOT_FOUND means the run is gone (closed, declined, or superseded) — a normal race, not a
    /// fault.</para></summary>
    Task<Result<DryRunCompletion, IpcError>> GetRunPlanStreamAsync(
        Guid runId, IDryRunChunkSink sink, CancellationToken ct = default);

    /// <summary>Approves a planned run (starts the work) or declines it (closes it, changing nothing).
    /// <para><paramref name="acknowledgeWarnings"/> confirms the blocking warnings the run's
    /// <c>run-planned</c> event listed. Without it an approval of a run whose profile raises any is
    /// refused as RUN_NOT_APPROVABLE — which is the point: a run planned from an unsaved draft never
    /// passed the save path's acknowledgment. Ignored when declining.</para></summary>
    Task<Result<bool, IpcError>> ApproveRunAsync(
        Guid runId, bool approve, bool acknowledgeWarnings = false, CancellationToken ct = default);

    /// <summary>Cancels a run. Work not yet started is dropped; work in flight finishes.</summary>
    Task<Result<bool, IpcError>> CancelRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Sets the global pause flag. The service publishes <c>pause-changed</c> only on an
    /// actual transition, so a caller must treat this ack — not an echoed event — as its confirmation.</summary>
    Task<Result<bool, IpcError>> SetPausedAsync(bool paused, CancellationToken ct = default);

    /// <summary>Recent jobs, newest first. The service's ring is in-memory (max 500) and is wiped on
    /// service restart, so an empty list is a legitimate answer, not an error.</summary>
    Task<Result<IReadOnlyList<JobSummaryDto>, IpcError>> GetRecentJobsAsync(
        int count = 50, CancellationToken ct = default);

    /// <summary>Per-job drill-down log lines. JOB_LOG_NOT_FOUND is EXPECTED for a job skipped before
    /// its journal was opened (source vanished under the lock, transformer refusal) — those never write
    /// a log file, so render it as "no log for this job" rather than as a failure.</summary>
    Task<Result<IReadOnlyList<string>, IpcError>> GetJobLogAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>The engine event stream, on its OWN connection (after subscribing a connection is
    /// one-way and must never carry a request). Each parsed event is a Success item; a refused
    /// subscription, a transport fault, or an unreadable event yields exactly ONE Failure item and then
    /// the sequence ends. A clean close or cancellation ends it with no Failure item.
    /// <para>Delivery is LOSSY — each subscriber has a bounded, drop-oldest frame channel — so the
    /// stream is not authoritative; reconcile with <see cref="GetRecentJobsAsync"/> and
    /// <see cref="GetStatusAsync"/> on every (re)connect. This method makes ONE attempt and does not
    /// retry: <c>EngineEventPump</c> owns the reconnect policy.</para></summary>
    IAsyncEnumerable<Result<EngineEvent, IpcError>> SubscribeEventsAsync(CancellationToken ct = default);
}
