using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.IPC;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GetStatusRequest), "get-status")]
[JsonDerivedType(typeof(ListProfilesRequest), "list-profiles")]
[JsonDerivedType(typeof(GetProfileRequest), "get-profile")]
[JsonDerivedType(typeof(SaveProfileRequest), "save-profile")]
[JsonDerivedType(typeof(DeleteProfileRequest), "delete-profile")]
[JsonDerivedType(typeof(ValidateProfileRequest), "validate-profile")]
[JsonDerivedType(typeof(GetMatchingProfilesRequest), "get-matching")]
[JsonDerivedType(typeof(RunProfileRequest), "run-profile")]
[JsonDerivedType(typeof(SetPausedRequest), "set-paused")]
[JsonDerivedType(typeof(DryRunRequest), "dry-run")]
[JsonDerivedType(typeof(DryRunStreamRequest), "dry-run-stream")]
[JsonDerivedType(typeof(GetRecentJobsRequest), "get-recent-jobs")]
[JsonDerivedType(typeof(GetJobLogRequest), "get-job-log")]
[JsonDerivedType(typeof(SubscribeEventsRequest), "subscribe")]
[JsonDerivedType(typeof(GetSettingsRequest), "get-settings")]
[JsonDerivedType(typeof(UpdateSettingsRequest), "update-settings")]
[JsonDerivedType(typeof(RelocateProfilesRequest), "relocate-profiles")]
[JsonDerivedType(typeof(ShutdownRequest), "shutdown")]
[JsonDerivedType(typeof(ApproveRunRequest), "approve-run")]
[JsonDerivedType(typeof(CancelRunRequest), "cancel-run")]
[JsonDerivedType(typeof(GetRunPlanStreamRequest), "get-run-plan-stream")]
public abstract record IpcRequest
{
    /// <summary>The protocol this build speaks. History: 1 — original wire format; 2 — dry-run
    /// chunks normalized against a shared directory table (DryRunDirectory/DryRunFile/
    /// DryRunOperation replace flat path strings); 3 — dry-run streams may interleave
    /// dry-run-progress frames before the terminator; 4 — dry-run requests may carry an inline
    /// Profile draft (InlineProfile) so unsaved edits can be previewed without a catalog lookup;
    /// 5 — relocate-profiles request added, answered with a relocate-profiles-result frame;
    /// 6 — job-progress engine events may interleave the job lifecycle stream, run-profile answers
    /// with a run-profile-result frame instead of a bare ok, and a folder run's final queued count
    /// (including zero for "nothing matched") arrives as a run-queued event;
    /// 7 — GlobalSettings dropped ThemeMode (settings.json schema v5): the theme is client-side state
    /// the engine never read, so it moved to the UI's own client-settings.json. Also adds
    /// EngineStatusSnapshot.ExecutablePath, so a client can tell WHICH executable is serving it, and
    /// EngineStatusSnapshot.StartupWarning, which carries a degraded-startup problem to clients that
    /// connect after the corresponding engine-warning event was published;
    /// 8 — DryRunChunkResponse became columnar: one array per field (DirectoryName/DirectoryParentIndex,
    /// and SourceFiles/DestinationFiles/SourceOperations/DestinationOperations as column groups) instead
    /// of a list of DryRunDirectory/DryRunFile/DryRunOperation objects. Measured on a 20,000-file chunk:
    /// the frame drops 7.39 MB to 3.62 MB (a record-wise encoding repeats every property name once per
    /// record, and those names were ~55% of the payload) and client-side deserialization allocates
    /// 13.31 MB instead of 19.72 MB. See docs/dry-run-ui-memory-next-steps.md for the full figures. This
    /// is a breaking wire change with no compatible reading — an old client would see every collection
    /// as absent and render an empty preview, which is exactly the silent-wrong-answer this version
    /// gate exists to prevent.
    /// A mismatched service/UI pair must fail loud (IPC_VERSION_MISMATCH), never half-parse.
    /// 9 — manual runs became snapshot-driven and two-phase. run-profile's Path is now OPTIONAL (null
    /// means the whole profile, which is what SyncMode.Mirror needs: its orphan set is only sound over
    /// the complete source set) and its reply carries a RunId that is now a real run rather than just a
    /// correlation token. New approve-run / cancel-run requests, and get-run-plan-stream replays a
    /// pending run's frozen work list as the same DryRunChunkResponse frames a preview uses. New
    /// run-planned / run-progress / run-completed engine events: an old client hitting an unknown
    /// EngineEvent discriminator throws inside System.Text.Json and loses its whole subscribe stream,
    /// which is precisely the half-parse this gate exists to prevent. (Engine-internal, NOT on the wire:
    /// Payload.RunId, JobCompletion.ResolvedFinalPaths, ITriggerQueue.Enqueue's return value.)</summary>
    public const int CurrentProtocolVersion = 9;

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
}

public sealed record GetStatusRequest : IpcRequest;
public sealed record ListProfilesRequest : IpcRequest;
public sealed record GetProfileRequest : IpcRequest { public required Guid ProfileId { get; init; } }
public sealed record SaveProfileRequest : IpcRequest
{
    public required Profile Profile { get; init; }
    /// <summary>Set when the user confirmed blocking warnings (e.g. PROFILE_UNVERIFIED_DELETE, §4.1).</summary>
    public bool AcknowledgeWarnings { get; init; }
}
public sealed record DeleteProfileRequest : IpcRequest { public required Guid ProfileId { get; init; } }
public sealed record ValidateProfileRequest : IpcRequest { public required Profile Profile { get; init; } }
public sealed record GetMatchingProfilesRequest : IpcRequest { public required string Path { get; init; } }
public sealed record RunProfileRequest : IpcRequest
{
    public required Guid ProfileId { get; init; }

    /// <summary>A file or folder to narrow the run to, or <b>null for the whole profile</b> (every
    /// Source).
    /// <para>Null is the normal case and what the GUI sends. It is also a requirement rather than a
    /// convenience under <see cref="Profiles.SyncMode.Mirror"/>: an orphan is "a destination file no
    /// source writes to", which can only be decided over the COMPLETE source set, so a narrowed run
    /// refuses to delete anything.</para></summary>
    public string? Path { get; init; }
}

/// <summary>Approves (or declines) a run that finished planning and is waiting. Declining closes the
/// run and changes nothing — at that point nothing has been touched.</summary>
public sealed record ApproveRunRequest : IpcRequest
{
    public required Guid RunId { get; init; }
    public required bool Approve { get; init; }
}

/// <summary>Cancels a run at any phase. Planning stops, pending work is dropped, and the orphan-deletion
/// phase is skipped. Jobs already in flight are never interrupted (I-ATOMIC-JOB).</summary>
public sealed record CancelRunRequest : IpcRequest
{
    public required Guid RunId { get; init; }
}

/// <summary>Replays a pending run's frozen work list, as the same <c>DryRunChunkResponse</c> frames a
/// preview streams. Deliberately the same wire shape: the approval view IS the dry-run view, so the
/// client needs no second renderer, and what the user approves is displayed by the code path they
/// already trust.</summary>
public sealed record GetRunPlanStreamRequest : IpcRequest
{
    public required Guid RunId { get; init; }
}
public sealed record SetPausedRequest : IpcRequest { public required bool Paused { get; init; } }
public sealed record DryRunRequest : IpcRequest
{
    public required Guid ProfileId { get; init; }
    public string? ScopePath { get; init; }
    /// <summary>When set, the run previews this in-memory draft (unsaved edits) directly instead of
    /// resolving <see cref="ProfileId"/> against the persisted catalog. Its Id should match ProfileId.</summary>
    public Profile? InlineProfile { get; init; }
}
/// <summary>Same inputs as <see cref="DryRunRequest"/>, but the report streams back as a sequence
/// of DryRunChunkResponse frames terminated by a DryRunCompleteResponse (see IpcResponse) — so a
/// report is no longer bounded by the single-frame size cap.</summary>
public sealed record DryRunStreamRequest : IpcRequest
{
    public required Guid ProfileId { get; init; }
    public string? ScopePath { get; init; }
    /// <summary>When set, the run previews this in-memory draft (unsaved edits) directly instead of
    /// resolving <see cref="ProfileId"/> against the persisted catalog. Its Id should match ProfileId.</summary>
    public Profile? InlineProfile { get; init; }
}
public sealed record GetRecentJobsRequest : IpcRequest { public int Count { get; init; } = 50; }
public sealed record GetJobLogRequest : IpcRequest { public required Guid JobId { get; init; } }
public sealed record SubscribeEventsRequest : IpcRequest;
public sealed record GetSettingsRequest : IpcRequest;
public sealed record UpdateSettingsRequest : IpcRequest { public required GlobalSettings Settings { get; init; } }
/// <summary>Relocates the profiles storage directory (persisted in settings). When
/// <see cref="MoveExisting"/> is set, the service moves the existing profile files into the new
/// directory before switching; otherwise it starts using the new (possibly empty) directory and
/// leaves the old files in place. Answered with a <see cref="RelocateProfilesResponse"/> carrying
/// the persisted settings (their <c>ProfilesDirectory</c> reflects the new, normalized location)
/// plus what happened to the existing files.</summary>
public sealed record RelocateProfilesRequest : IpcRequest
{
    public required string NewDirectory { get; init; }
    public bool MoveExisting { get; init; }
}
/// <summary>Asks the service to shut itself down gracefully (StartAndStopWithProgram mode on UI close).</summary>
public sealed record ShutdownRequest : IpcRequest;