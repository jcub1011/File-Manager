using FileManager.Contracts.Primitives;
using FileManager.Core.Journal;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FileManager.Core.Audit;

/// <summary>An append-only, fsync'd NDJSON audit trail in monthly files, under the §5.5 framing.
///
/// <para>Extracted because the two trails — <see cref="DispositionAuditLog"/> and
/// <see cref="MirrorDeletionAuditLog"/> — had this same 130 lines twice over, differing only in their
/// record type and their file prefix. Everything here is load-bearing in a way that makes duplication
/// expensive: the fsync per record is what makes the trail survive the crash it describes, the tail read
/// is what stops an unbounded file from being loaded whole, and skipping a malformed line rather than
/// failing the read is what keeps one corrupt record from hiding every good one. A fix to any of those —
/// or a bug in the newline scan — would otherwise have to be made twice and would silently diverge in
/// one trail.</para>
///
/// <para>Records are never auto-deleted. Spec §7 calls this the no-loss safety net.</para></summary>
public abstract class NdjsonAuditLog<T>(
    EnginePaths paths, string filePrefix, JsonTypeInfo<T> typeInfo, ILogger logger)
    where T : class
{
    /// <summary>Bound on a tail read: a monthly file is never auto-deleted and can grow without limit,
    /// but the only read this trail supports is "the newest N records".</summary>
    private const long MaxTailBytes = 1 << 20;   // 1 MiB

    private readonly object _gate = new();

    /// <summary>When the record happened, for choosing its monthly file and ordering the tail. A property
    /// of the record rather than the clock: a record must land in the file for the month it describes even
    /// if it is written just after midnight.</summary>
    protected abstract DateTimeOffset TimestampOf(T record);

    /// <summary>What this trail is called in its log lines and error messages, lower case — "audit",
    /// "mirror deletion audit". These reach the user through a failed IPC reply, so the two trails stay
    /// distinguishable in a report even though the code is shared.</summary>
    protected abstract string TrailName { get; }

    /// <summary>Identifies the record in an append-failure log line: the path the row is about.</summary>
    protected abstract string SubjectOf(T record);

    protected Result AppendCore(T record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.AuditDirectory);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, typeInfo);
                byte[] line = NdjsonFrame.Encode(json);
                using FileStream stream = new(
                    MonthlyPath(TimestampOf(record)), FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(line, 0, line.Length);
                // fsync per record: the whole value of this file is that it survives the crash that
                // interrupted the operation it describes.
                stream.Flush(flushToDisk: true);
                return Result.Success();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Trail} append failed for {Path}", TrailName, SubjectOf(record));
                return $"{TrailName} append failed: {ex.Message}";
            }
        }
    }

    protected Result<IReadOnlyList<T>, string> ReadRecentCore(int count)
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(paths.AuditDirectory))
                    return Result<IReadOnlyList<T>, string>.Success([]);

                // Newest months first; read enough files to satisfy `count`, then take the tail.
                List<T> records = [];
                IEnumerable<string> files = Directory
                    .EnumerateFiles(paths.AuditDirectory, $"{filePrefix}-*.ndjsonl")
                    .OrderByDescending(p => p, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    records.AddRange(ReadFile(file));
                    if (records.Count >= count)
                        break;
                }
                IReadOnlyList<T> tail = [.. records.OrderByDescending(TimestampOf).Take(count)];
                return Result<IReadOnlyList<T>, string>.Success(tail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Trail} read failed", TrailName);
                return $"{TrailName} read failed: {ex.Message}";
            }
        }
    }

    private IEnumerable<T> ReadFile(string path)
    {
        byte[] bytes = NdjsonLines.ReadTail(path, MaxTailBytes, out int start);
        foreach ((int offset, int length) in NdjsonLines.Spans(bytes, start))
        {
            if (TryParse(bytes.AsSpan(offset, length)) is { } record)
                yield return record;
        }
    }

    private T? TryParse(ReadOnlySpan<byte> line)
    {
        if (!NdjsonFrame.TryDecode(line, out ReadOnlySpan<byte> json, out string? reason))
        {
            logger.LogWarning("Skipping malformed {Trail} record: {Reason}", TrailName, reason);
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping unparseable {Trail} record", TrailName);
            return null;
        }
    }

    private string MonthlyPath(DateTimeOffset at) =>
        Path.Combine(paths.AuditDirectory, $"{filePrefix}-{at.UtcDateTime:yyyyMM}.ndjsonl");
}
