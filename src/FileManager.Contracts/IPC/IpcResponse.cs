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
[JsonDerivedType(typeof(DryRunProgressResponse), "dry-run-progress")]
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
/// zero or more of these, each well under the frame cap, then a single
/// <see cref="DryRunCompleteResponse"/> terminator. Each carries a slice of the four report
/// collections; the client appends them <b>in receive order</b> so the file lists' indices stay
/// global (an op's SourceIndex/SubjectIndex is a position into the fully assembled lists). Sweep
/// frames leave the source collections empty; their <c>SubjectIndex</c> values are already offset
/// by the running <see cref="DestinationFiles"/> count.
/// <para>
/// Paths are normalized against a shared directory table: <see cref="Directories"/> carries, in
/// global index order, exactly the <see cref="DryRunDirectory"/> entries first referenced by this
/// chunk — every <c>ParentIndex</c> refers to an earlier global index (possibly a prior chunk's),
/// and every <c>DirIndex</c>/<c>RootDirIndex</c> in this chunk's files/ops is below the table count
/// after this chunk's entries are appended. The consumer appends <see cref="Directories"/> before
/// reading the files/ops, exactly as it already appends the file lists.
/// </para></summary>
public sealed record DryRunChunkResponse : IpcResponse
{
    public IReadOnlyList<DryRunDirectory> Directories { get; init; } = [];
    public IReadOnlyList<DryRunFile> SourceFiles { get; init; } = [];
    public IReadOnlyList<DryRunFile> DestinationFiles { get; init; } = [];
    public IReadOnlyList<DryRunOperation> SourceOperations { get; init; } = [];
    public IReadOnlyList<DryRunOperation> DestinationOperations { get; init; } = [];
}
/// <summary>An informational progress snapshot interleaved in a streamed dry run. Zero or more may
/// appear anywhere in the stream before the <see cref="DryRunCompleteResponse"/> terminator; they
/// carry no report data and do not affect chunk reassembly. Counts are cumulative files discovered
/// so far (see <see cref="DryRunProgress"/>).</summary>
public sealed record DryRunProgressResponse : IpcResponse
{
    public required DryRunProgressPhase Phase { get; init; }
    public required long SourceFiles { get; init; }
    public required long DestinationFiles { get; init; }
}
/// <summary>Terminates a streamed dry-run report. GeneratedAt is stamped when the report finishes;
/// Truncated is true only if a service-side safety bound cut the report short.</summary>
public sealed record DryRunCompleteResponse : IpcResponse
{
    public required System.DateTimeOffset GeneratedAt { get; init; }
    public required bool Truncated { get; init; }

    /// <summary>The whole-report byte/space projection, computed service-side after assembly. Null on
    /// a truncated report (unsound over a partial graph) or when a volume set could not be resolved.</summary>
    public SpaceProjection? Space { get; init; }
}
public sealed record RecentJobsResponse : IpcResponse { public required IReadOnlyList<JobSummaryDto> Jobs { get; init; } }
public sealed record JobLogResponse : IpcResponse { public required IReadOnlyList<string> Lines { get; init; } }
public sealed record SettingsResponse : IpcResponse { public required GlobalSettings Settings { get; init; } }
