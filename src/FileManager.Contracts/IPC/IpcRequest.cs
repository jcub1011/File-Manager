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
public abstract record IpcRequest
{
    /// <summary>The protocol this build speaks. History: 1 — original wire format; 2 — dry-run
    /// chunks normalized against a shared directory table (DryRunDirectory/DryRunFile/
    /// DryRunOperation replace flat path strings); 3 — dry-run streams may interleave
    /// dry-run-progress frames before the terminator; 4 — dry-run requests may carry an inline
    /// Profile draft (InlineProfile) so unsaved edits can be previewed without a catalog lookup;
    /// 5 — relocate-profiles request added, answered with a relocate-profiles-result frame.
    /// A mismatched service/UI pair must fail loud (IPC_VERSION_MISMATCH), never half-parse.</summary>
    public const int CurrentProtocolVersion = 5;

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
    public required string Path { get; init; }        // file or folder (folder → recursive per MaxDepth)
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