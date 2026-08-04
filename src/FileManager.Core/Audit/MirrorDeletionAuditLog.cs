using FileManager.Contracts.Primitives;
using FileManager.Core.Journal;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    : IReconcileAuditLog
{
    /// <summary>Bound on the tail read back: a monthly file is never auto-deleted and can grow large,
    /// but ReadRecent only ever needs the newest records.</summary>
    private const long MaxTailBytes = 1 << 20;

    private readonly object _gate = new();

    public Result Append(MirrorDeletionAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.AuditDirectory);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                    record, MirrorAuditJsonContext.Default.MirrorDeletionAuditRecord);
                byte[] line = NdjsonFrame.Encode(json);
                using FileStream stream = new(
                    MonthlyPath(record.AtUtc), FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(line, 0, line.Length);
                // fsync per record, as the disposition trail does: the whole value of this file is
                // that it survives the crash that interrupted the deletion it describes.
                stream.Flush(flushToDisk: true);
                return Result.Success();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mirror deletion audit append failed for {Path}", record.DestinationPath);
                return $"mirror deletion audit append failed: {ex.Message}";
            }
        }
    }

    public Result<IReadOnlyList<MirrorDeletionAuditRecord>, string> ReadRecent(int count)
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(paths.AuditDirectory))
                    return Result<IReadOnlyList<MirrorDeletionAuditRecord>, string>.Success([]);

                List<MirrorDeletionAuditRecord> records = [];
                IEnumerable<string> files = Directory
                    .EnumerateFiles(paths.AuditDirectory, "mirror-*.ndjsonl")
                    .OrderByDescending(p => p, StringComparer.Ordinal);   // newest month first
                foreach (string file in files)
                {
                    records.AddRange(ReadFile(file));
                    if (records.Count >= count)
                        break;
                }
                IReadOnlyList<MirrorDeletionAuditRecord> tail = [.. records
                    .OrderByDescending(r => r.AtUtc)
                    .Take(count)];
                return Result<IReadOnlyList<MirrorDeletionAuditRecord>, string>.Success(tail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mirror deletion audit read failed");
                return $"mirror deletion audit read failed: {ex.Message}";
            }
        }
    }

    private IEnumerable<MirrorDeletionAuditRecord> ReadFile(string path)
    {
        byte[] bytes;
        int start;
        using (FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
                MirrorDeletionAuditRecord? record = TryParse(bytes.AsSpan(start, len));
                if (record is not null)
                    yield return record;
            }
            start = i + 1;
        }
    }

    private MirrorDeletionAuditRecord? TryParse(ReadOnlySpan<byte> line)
    {
        if (!NdjsonFrame.TryDecode(line, out ReadOnlySpan<byte> json, out string? reason))
        {
            logger.LogWarning("Skipping malformed mirror deletion audit record: {Reason}", reason);
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(json, MirrorAuditJsonContext.Default.MirrorDeletionAuditRecord);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping unparseable mirror deletion audit record");
            return null;
        }
    }

    private string MonthlyPath(DateTimeOffset at) =>
        Path.Combine(paths.AuditDirectory, $"mirror-{at.UtcDateTime:yyyyMM}.ndjsonl");

    /// <summary>Where a trashed orphan went. Spec §3.1.1: a Mirror orphan is recycled, never hard
    /// deleted, so this is the only value written today.</summary>
    public const string RecycleBinDestination = "RecycleBin";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(MirrorDeletionAuditRecord))]
internal sealed partial class MirrorAuditJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
