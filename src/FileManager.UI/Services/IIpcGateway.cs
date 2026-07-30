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
    /// <summary>Runs a streamed dry run. <paramref name="progress"/> (when supplied) receives
    /// throttled discovery updates as the service reports them; construct the
    /// <see cref="Progress{T}"/> on the UI thread so reports marshal there automatically.
    /// When <paramref name="draft"/> is supplied, the service previews that in-memory profile
    /// (unsaved edits) directly instead of resolving <paramref name="profileId"/> against the
    /// persisted catalog.</summary>
    Task<Result<DryRunReport, IpcError>> DryRunAsync(
        Guid profileId, IProgress<DryRunProgress>? progress = null, Profile? draft = null,
        CancellationToken ct = default);
    Task<Result<GlobalSettings, IpcError>> GetSettingsAsync(CancellationToken ct = default);
    Task<Result<GlobalSettings, IpcError>> SaveSettingsAsync(GlobalSettings settings, CancellationToken ct = default);
    /// <summary>Relocates the profiles storage directory, optionally moving the existing files.
    /// Returns the persisted settings (their <see cref="GlobalSettings.ProfilesDirectory"/> reflects
    /// the new, normalized location) plus the moved/skipped file outcome — skipped files mean a
    /// same-name copy already at the destination won, which the caller must surface.</summary>
    Task<Result<RelocateProfilesResponse, IpcError>> RelocateProfilesAsync(
        string newDirectory, bool moveExisting, CancellationToken ct = default);
    /// <summary>Asks the service to shut itself down (StartAndStopWithProgram mode on UI close).</summary>
    Task<Result<bool, IpcError>> ShutdownServiceAsync(CancellationToken ct = default);

    /// <summary>Manually invokes a profile against one file or folder (a folder is walked recursively,
    /// honoring MaxDepth). The service enqueues and answers immediately, so the response means
    /// "accepted", never "finished": a single file yields an exact <c>QueuedCount</c>, while a folder
    /// yields <c>Scanning = true</c> and reports its final count later as a <see cref="RunQueuedEvent"/>.
    /// PROFILE_NOT_FOUND / PROFILE_INACTIVE / PATH_NOT_FOUND surface as an <see cref="IpcError"/>.</summary>
    Task<Result<RunProfileResponse, IpcError>> RunProfileAsync(
        Guid profileId, string path, CancellationToken ct = default);

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
