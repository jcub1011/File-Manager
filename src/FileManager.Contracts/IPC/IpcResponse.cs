using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System;
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
[JsonDerivedType(typeof(RunProfileResponse), "run-profile-result")]
[JsonDerivedType(typeof(DryRunResponse), "dry-run-report")]
[JsonDerivedType(typeof(DryRunChunkResponse), "dry-run-chunk")]
[JsonDerivedType(typeof(DryRunProgressResponse), "dry-run-progress")]
[JsonDerivedType(typeof(DryRunCompleteResponse), "dry-run-complete")]
[JsonDerivedType(typeof(RecentJobsResponse), "recent-jobs")]
[JsonDerivedType(typeof(JobLogResponse), "job-log")]
[JsonDerivedType(typeof(SettingsResponse), "settings")]
[JsonDerivedType(typeof(RelocateProfilesResponse), "relocate-profiles-result")]
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
/// <summary>Answer to a RunProfileRequest (§4.9). A single-file run is enqueued synchronously, so
/// <see cref="QueuedCount"/> is exact (1) and <see cref="Scanning"/> is false. A folder run starts a
/// background recursive enumeration so the reply stays prompt (§8 rule 5): Scanning is true,
/// QueuedCount is 0, and the final count — including 0 for "nothing matched" — arrives later as a
/// <see cref="RunQueuedEvent"/> for the same (ProfileId, ScopePath).
/// <para>Queued means <em>accepted</em>, not copied: the job's own filter gate (§4.3 step 4) may
/// still skip the file, and an identical run already pending is coalesced by the trigger queue.</para></summary>
public sealed record RunProfileResponse : IpcResponse
{
    public required int QueuedCount { get; init; }
    public required bool Scanning { get; init; }

    /// <summary>Correlates this reply with the <see cref="RunQueuedEvent"/> that later reports the
    /// enumeration's outcome. The event bus is a broadcast, so without a correlation id every connected
    /// client announced "Queued N file(s) from …" for runs it never requested.</summary>
    public required Guid RunId { get; init; }
}
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
/// <para><strong>Columnar, not one object per record</strong>, and this is the shape's whole point.
/// Measured on a 20,000-file chunk (58,340 wire records), against the previous list-of-objects form:
/// the frame shrinks <strong>7.39 MB → 3.62 MB (−52%)</strong>, because a record-wise encoding repeats
/// every property name once per record and those names were ~55% of the payload; and deserializing it
/// allocates <strong>19.72 MB → 13.31 MB (−33%)</strong>, because System.Text.Json no longer
/// materializes a <c>DryRunFile</c>/<c>DryRunOperation</c> per row. The client's store is itself
/// columnar, so folding a chunk becomes close to a bulk copy. It also lines the wire up with one
/// source-generated definition rather than needing a hand-written reader — see
/// <c>docs/dry-run-ui-memory-next-steps.md</c>.</para>
/// <para><strong>Columns within a group must be the same length</strong> — that is the one invariant
/// records gave for free and this shape does not. Ragged columns would silently misalign rows (a file's
/// name paired with another's directory), which in a preview a user approves destructive operations from
/// is a correctness fault, not a cosmetic one. <see cref="DryRunColumns.FindRaggedColumn"/> is the
/// trust-boundary check, and the client rejects a ragged chunk as <c>IPC_MALFORMED</c>.</para>
/// <para>Mutable <see cref="List{T}"/> columns rather than <c>init</c> arrays, deliberately and for the
/// same reason <see cref="DryRunFile"/> is a mutable class: the service reuses one chunk's column buffers
/// for the whole stream (<c>Clear</c> + refill) instead of allocating per chunk. A consumer that buffers
/// responses across chunks must ask the converter not to recycle.</para></summary>
public sealed record DryRunChunkResponse : IpcResponse
{
    /// <summary>The directory entries first referenced by this chunk, in global index order —
    /// <c>DirectoryName[i]</c> and <c>DirectoryParentIndex[i]</c> are one entry.</summary>
    public List<string> DirectoryName { get; set; } = [];
    public List<int> DirectoryParentIndex { get; set; } = [];
    public DryRunFileColumns SourceFiles { get; set; } = new();
    public DryRunFileColumns DestinationFiles { get; set; } = new();
    public DryRunOperationColumns SourceOperations { get; set; } = new();
    public DryRunOperationColumns DestinationOperations { get; set; } = new();
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
/// <summary>Answer to RelocateProfilesRequest: the persisted settings plus what happened to the
/// existing profile files. <see cref="SkippedFiles"/> lists file names left in the OLD directory
/// because the destination already had a file with that name — the destination's (possibly stale)
/// copy wins after the switch, so callers must surface these to the user, never report a clean
/// success over them.</summary>
public sealed record RelocateProfilesResponse : IpcResponse
{
    public required GlobalSettings Settings { get; init; }
    /// <summary>Profile files moved into the new directory (0 when MoveExisting was false).</summary>
    public int MovedCount { get; init; }
    /// <summary>File names (not full paths) left behind due to a same-name collision at the destination.</summary>
    public IReadOnlyList<string> SkippedFiles { get; init; } = [];
}
