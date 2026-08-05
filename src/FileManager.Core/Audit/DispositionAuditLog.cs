using FileManager.Contracts.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FileManager.Core.Audit;

/// <summary>Spec §7 deletion audit trail — durable, append-only, fsync'd NDJSON (the §5.5 framing,
/// via <see cref="NdjsonFrame"/>). Monthly files <c>audit-YYYYMM.ndjsonl</c>, never auto-deleted.</summary>
public sealed class DispositionAuditLog(EnginePaths paths, ILogger<DispositionAuditLog> logger)
    : NdjsonAuditLog<DispositionAuditRecord>(
        paths, "audit", AuditJsonContext.Default.DispositionAuditRecord, logger),
      IDispositionAuditLog
{
    protected override DateTimeOffset TimestampOf(DispositionAuditRecord record) => record.AtUtc;
    protected override string TrailName => "audit";
    protected override string SubjectOf(DispositionAuditRecord record) => record.SourcePath;

    public Result Append(DispositionAuditRecord record) => AppendCore(record);

    public Result<IReadOnlyList<DispositionAuditRecord>, string> ReadRecent(int count) =>
        ReadRecentCore(count);
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DispositionAuditRecord))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
