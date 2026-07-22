using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Audit;

public interface IDispositionAuditLog
{
    Result Append(DispositionAuditRecord record);
    Result<IReadOnlyList<DispositionAuditRecord>, string> ReadRecent(int count);
}

public sealed record DispositionAuditRecord(
    Guid JobId, string SourcePath, OnSuccessAction Action, string? Destination, DateTimeOffset AtUtc);
