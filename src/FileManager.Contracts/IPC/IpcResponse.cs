using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Contracts.IPC;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OkResponse), "ok")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
[JsonDerivedType(typeof(StatusResponse), "status")]
[JsonDerivedType(typeof(ProfileListResponse), "profile-list")]
[JsonDerivedType(typeof(ProfileResponse), "profile")]
[JsonDerivedType(typeof(ValidationResponse), "validation")]
[JsonDerivedType(typeof(MatchingProfilesResponse), "matching")]
[JsonDerivedType(typeof(DryRunResponse), "dry-run-report")]
[JsonDerivedType(typeof(DryRunChunkResponse), "dry-run-chunk")]
[JsonDerivedType(typeof(DryRunDestinationChunkResponse), "dry-run-dest-chunk")]
[JsonDerivedType(typeof(DryRunCompleteResponse), "dry-run-complete")]
[JsonDerivedType(typeof(RecentJobsResponse), "recent-jobs")]
[JsonDerivedType(typeof(JobLogResponse), "job-log")]
[JsonDerivedType(typeof(SettingsResponse), "settings")]
public abstract record IpcResponse;

public sealed record OkResponse : IpcResponse;
public sealed record ErrorResponse : IpcResponse
{
    public required string Code { get; init; }        // e.g. "IPC_VERSION_MISMATCH", "PROFILE_NOT_FOUND"
    public required string Message { get; init; }
}
public sealed record StatusResponse : IpcResponse { public required EngineStatusSnapshot Status { get; init; } }
public sealed record ProfileListResponse : IpcResponse { public required IReadOnlyList<ProfileSummary> Profiles { get; init; } }
public sealed record ProfileResponse : IpcResponse { public required Profile Profile { get; init; } }
public sealed record ValidationResponse : IpcResponse { public required IReadOnlyList<ValidationIssue> Issues { get; init; } }
public sealed record MatchingProfilesResponse : IpcResponse { public required IReadOnlyList<ProfileMatchDto> Matches { get; init; } }
public sealed record DryRunResponse : IpcResponse { public required DryRunReport Report { get; init; } }
/// <summary>One batch of a streamed dry-run report (see DryRunStreamRequest). The service sends
/// zero or more of these, in source-path order, each well under the frame cap, then a single
/// <see cref="DryRunCompleteResponse"/> terminator.</summary>
public sealed record DryRunChunkResponse : IpcResponse { public required IReadOnlyList<DryRunFileResult> Files { get; init; } }
/// <summary>One batch of destination-only entries (pre-existing Untouched files + Mirror orphans),
/// streamed after the file chunks and before the <see cref="DryRunCompleteResponse"/> terminator.
/// The write side is derived client-side from the file chunks' target actions, so this carries
/// only what those can't express.</summary>
public sealed record DryRunDestinationChunkResponse : IpcResponse { public required IReadOnlyList<DryRunDestinationEntry> Entries { get; init; } }
/// <summary>Terminates a streamed dry-run report. GeneratedAt is stamped when the report finishes;
/// Truncated is true only if a service-side safety bound cut the report short.</summary>
public sealed record DryRunCompleteResponse : IpcResponse
{
    public required System.DateTimeOffset GeneratedAt { get; init; }
    public required bool Truncated { get; init; }
}
public sealed record RecentJobsResponse : IpcResponse { public required IReadOnlyList<JobSummaryDto> Jobs { get; init; } }
public sealed record JobLogResponse : IpcResponse { public required IReadOnlyList<string> Lines { get; init; } }
public sealed record SettingsResponse : IpcResponse { public required GlobalSettings Settings { get; init; } }
