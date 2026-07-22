using FileManager.Contracts.Primitives;
using FileManager.Core.Journal;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileManager.Core.Audit;

/// <summary>Spec §7 deletion audit trail — durable, append-only, fsync'd NDJSON (the §5.5 framing,
/// via <see cref="NdjsonFrame"/>). Monthly files <c>audit-YYYYMM.ndjsonl</c>, never auto-deleted.</summary>
public sealed class DispositionAuditLog(EnginePaths paths, ILogger<DispositionAuditLog> logger) : IDispositionAuditLog
{
    private readonly object _gate = new();

    public Result Append(DispositionAuditRecord record)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.AuditDirectory);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, AuditJsonContext.Default.DispositionAuditRecord);
                byte[] line = NdjsonFrame.Encode(json);
                using FileStream stream = new(MonthlyPath(record.AtUtc), FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(line, 0, line.Length);
                stream.Flush(flushToDisk: true);
                return Result.Success();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit append failed for {Path}", record.SourcePath);
                return Result.Failure($"audit append failed: {ex.Message}");
            }
        }
    }

    public Result<IReadOnlyList<DispositionAuditRecord>, string> ReadRecent(int count)
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(paths.AuditDirectory))
                    return Result<IReadOnlyList<DispositionAuditRecord>, string>.Success([]);

                // Newest months first; read enough files to satisfy `count`, then take the tail.
                var records = new List<DispositionAuditRecord>();
                IEnumerable<string> files = Directory.EnumerateFiles(paths.AuditDirectory, "audit-*.ndjsonl")
                    .OrderByDescending(p => p, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    records.AddRange(ReadFile(file));
                    if (records.Count >= count)
                        break;
                }
                IReadOnlyList<DispositionAuditRecord> tail = records
                    .OrderByDescending(r => r.AtUtc)
                    .Take(count)
                    .ToList();
                return Result<IReadOnlyList<DispositionAuditRecord>, string>.Success(tail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit read failed");
                return $"audit read failed: {ex.Message}";
            }
        }
    }

    private IEnumerable<DispositionAuditRecord> ReadFile(string path)
    {
        // Bound memory: a monthly audit file is never auto-deleted and can grow large, but ReadRecent
        // only ever needs the newest records — so read at most the tail and start on a line boundary.
        const long MaxTailBytes = 1 << 20;   // 1 MiB

        byte[] bytes;
        int start;
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long length = stream.Length;
            if (length <= MaxTailBytes)
            {
                bytes = new byte[(int)length];
                stream.ReadExactly(bytes);
                start = 0;
            }
            else
            {
                stream.Seek(length - MaxTailBytes, SeekOrigin.Begin);
                bytes = new byte[(int)MaxTailBytes];
                stream.ReadExactly(bytes);
                // Drop the (likely partial) first line so parsing begins at a complete record.
                int nl = Array.IndexOf(bytes, (byte)'\n');
                start = nl >= 0 ? nl + 1 : bytes.Length;
            }
        }

        for (int i = start; i <= bytes.Length; i++)
        {
            bool end = i == bytes.Length;
            if (!end && bytes[i] != (byte)'\n')
                continue;
            int len = i - start;
            if (len > 0)
            {
                DispositionAuditRecord? record = TryParse(bytes.AsSpan(start, len));
                if (record is not null)
                    yield return record;
            }
            start = i + 1;
        }
    }

    private DispositionAuditRecord? TryParse(ReadOnlySpan<byte> line)
    {
        if (!NdjsonFrame.TryDecode(line, out ReadOnlySpan<byte> json, out string? reason))
        {
            logger.LogWarning("Skipping malformed audit record: {Reason}", reason);
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(json, AuditJsonContext.Default.DispositionAuditRecord);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping unparseable audit record");
            return null;
        }
    }

    private string MonthlyPath(DateTimeOffset at) =>
        Path.Combine(paths.AuditDirectory, $"audit-{at.UtcDateTime:yyyyMM}.ndjsonl");
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DispositionAuditRecord))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
