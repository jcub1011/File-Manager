using FileManager.Contracts.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Audit;

public interface IReconcileAuditLog
{
    Result Append(MirrorDeletionAuditRecord record);
    Result<IReadOnlyList<MirrorDeletionAuditRecord>, string> ReadRecent(int count);
}

/// <summary>One destination file a <see cref="Contracts.Profiles.SyncMode.Mirror"/> run removed.
///
/// <para><b>Deliberately NOT a <see cref="DispositionAuditRecord"/>.</b> That record answers "what
/// happened to my originals" — its <c>SourcePath</c> is a source, and its <c>Action</c> is the
/// profile's <c>OnSuccess</c> policy. A mirror orphan is neither: putting a destination path in
/// <c>SourcePath</c> and stamping <c>MoveToTrash</c> as the action would make "the mirror removed a
/// stale copy from your backup" indistinguishable from "your OnSuccess policy recycled your original".
/// Spec §7 calls this trail the no-loss safety net, and a safety net that records a plausible untruth
/// is worse than one with a second file in it. (The repo has already been bitten by exactly this
/// shape: <c>MoveToArchive</c> once wrote an audit row claiming success for a move that was a silent
/// no-op.)</para>
///
/// <para><see cref="Destination"/> is a field rather than a constant even though spec §3.1.1 forbids
/// anything but the Recycle Bin today, so a future FreeDesktop trash — or a configurable sink — is
/// additive rather than a schema change.</para></summary>
public sealed record MirrorDeletionAuditRecord(
    Guid PassId, Guid ProfileId, Guid RunId, string DestinationPath, string TargetRoot,
    long SizeBytes, string Destination, DateTimeOffset AtUtc);

/// <summary>The Mirror deletion trail: durable, append-only, fsync'd NDJSON under the §5.5 framing —
/// the same substrate and guarantees as <see cref="DispositionAuditLog"/>, in its own monthly files
/// (<c>mirror-YYYYMM.ndjsonl</c>) so the two trails never have to be told apart by inspecting fields.
/// Never auto-deleted.</summary>
public sealed class MirrorDeletionAuditLog(EnginePaths paths, ILogger<MirrorDeletionAuditLog> logger)
    : NdjsonAuditLog<MirrorDeletionAuditRecord>(
        paths, "mirror", MirrorAuditJsonContext.Default.MirrorDeletionAuditRecord, logger),
      IReconcileAuditLog
{
    protected override DateTimeOffset TimestampOf(MirrorDeletionAuditRecord record) => record.AtUtc;
    protected override string TrailName => "mirror deletion audit";
    protected override string SubjectOf(MirrorDeletionAuditRecord record) => record.DestinationPath;

    public Result Append(MirrorDeletionAuditRecord record) => AppendCore(record);

    public Result<IReadOnlyList<MirrorDeletionAuditRecord>, string> ReadRecent(int count) =>
        ReadRecentCore(count);

    /// <summary>Where a trashed orphan went. Spec §3.1.1: a Mirror orphan is recycled, never hard
    /// deleted, so this is the only value written today.</summary>
    public const string RecycleBinDestination = "RecycleBin";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(MirrorDeletionAuditRecord))]
internal sealed partial class MirrorAuditJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
