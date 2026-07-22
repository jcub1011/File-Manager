using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileManager.Core.Journal;

/// <summary>The durable, append-only, fsync'd write-ahead journal (§5.5, §6.3). Every
/// <see cref="Append"/> writes one CRC-framed NDJSON line and <c>Flush(flushToDisk: true)</c>
/// before returning — that flush is the durability contract. Segments are named
/// <c>journal-000001.ndjsonl</c> and rotate at <see cref="EngineConfig.JournalRotateAtBytes"/>.
///
/// Framing per line: <c>J1 &lt;crc:8-hex&gt; &lt;json&gt;\n</c>, checksummed with CRC-32 (IEEE) via
/// .NET's System.IO.Hashing. The exact polynomial is not load-bearing — the same function guards
/// write and read, giving the torn-write detection §5.5 requires. See <c>NdjsonFrame</c> for the
/// CRC-32-vs-CRC-32C rationale.</summary>
public sealed class JobJournal : IJobJournal, IDisposable
{
    private readonly EnginePaths _paths;
    private readonly EngineConfig _config;
    private readonly ILogger<JobJournal> _logger;
    private readonly object _gate = new();

    private FileStream? _active;
    private int _activeNumber;
    private long _nextSeq = 1;
    private bool _initialized;

    public JobJournal(EnginePaths paths, EngineConfig config, ILogger<JobJournal> logger)
    {
        _paths = paths;
        _config = config;
        _logger = logger;
    }

    public Result Append(JournalRecord record)
    {
        lock (_gate)
        {
            try
            {
                EnsureInitialized();
                // The journal owns Seq (monotonic per writer); the caller's placeholder is replaced.
                JournalRecord stamped = record with { Seq = _nextSeq };
                byte[] line = FrameLine(stamped);
                _active!.Write(line, 0, line.Length);
                _active.Flush(flushToDisk: true);
                _nextSeq++;

                // The record is now durable (written + fsync'd). Auto-rotation is best-effort
                // compaction — a rotation failure must NOT turn a persisted append into a Result
                // the caller would translate to JournalWriteFailed and roll back a live job over.
                if (_active.Length > _config.JournalRotateAtBytes)
                {
                    try
                    {
                        RotateLocked();
                    }
                    catch (Exception rotateEx)
                    {
                        _logger.LogWarning(rotateEx, "Journal auto-rotation failed after a durable append; segment left un-rotated");
                    }
                }
                return Result.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Journal append failed for job {JobId} ({Type})", record.JobId, record.GetType().Name);
                // Surface as a JobError-shaped failure via the void Result's message; typed callers
                // translate a failed append into JobErrorCode.JournalWriteFailed and roll back.
                return Result.Failure($"journal append failed: {ex.Message}");
            }
        }
    }

    public Result<IReadOnlyList<JournalRecord>, JobError> ReadAll()
    {
        lock (_gate)
        {
            try
            {
                EnsureDirectory();
                List<(int Number, string Path)> segments = OrderedSegments();
                var records = new List<JournalRecord>();
                for (int s = 0; s < segments.Count; s++)
                {
                    bool isLastSegment = s == segments.Count - 1;
                    ReadSegment(segments[s].Path, isLastSegment, records);
                }
                return records;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Journal read failed");
                return new JobError { Code = JobErrorCode.JournalWriteFailed, Message = $"journal read failed: {ex.Message}" };
            }
        }
    }

    public Result Rotate()
    {
        lock (_gate)
        {
            try
            {
                EnsureInitialized();
                RotateLocked();
                return Result.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Journal rotation failed");
                return Result.Failure($"journal rotation failed: {ex.Message}");
            }
        }
    }

    // --- internals (all under _gate) ---

    private void EnsureInitialized()
    {
        if (_initialized)
            return;
        EnsureDirectory();

        List<(int Number, string Path)> segments = OrderedSegments();
        var existing = new List<JournalRecord>();
        foreach ((int _, string path) in segments)
            ReadSegment(path, isLastSegment: true, existing);   // seed scan tolerates a torn tail anywhere
        if (existing.Count > 0)
            _nextSeq = existing.Max(r => r.Seq) + 1;

        _activeNumber = segments.Count > 0 ? segments[^1].Number : 1;
        _active = OpenAppend(SegmentPath(_activeNumber));
        _initialized = true;
    }

    private void RotateLocked()
    {
        // Copy every still-OPEN job's records forward into a fresh segment, then delete the old
        // ones (I-APPEND — nothing is ever edited in place).
        //
        // Crash-atomicity contract: rotation is write-new-then-delete-old and is NOT atomic. A crash
        // between the two steps leaves both the new segment and one or more old segments on disk, so
        // ReadAll returns DUPLICATE records for the still-open jobs. Recovery tolerates this by design
        // — it groups records by JobId, takes the FIRST job-opened, and Promote() only ever advances
        // target state monotonically, so replaying a record twice is idempotent. (Covered by the
        // duplicate-across-segments replay test.) Do not "optimise" recovery in a way that breaks this.
        //
        // Durability limitation (v1, accepted): Flush(flushToDisk:true) makes each segment's DATA
        // durable, but the directory ENTRY for a freshly created segment is not separately fsync'd —
        // Windows/.NET has no portable directory-fsync, so a power loss immediately after creating the
        // very first segment could in principle lose that segment. The duplicate-tolerant recovery
        // above bounds the blast radius; a full directory fsync is deferred to the platform layer.
        List<(int Number, string Path)> segments = OrderedSegments();
        var all = new List<JournalRecord>();
        foreach ((int _, string path) in segments)
            ReadSegment(path, isLastSegment: true, all);

        HashSet<Guid> closed = all.OfType<JobClosedRecord>().Select(r => r.JobId).ToHashSet();
        List<JournalRecord> openRecords = all
            .Where(r => !closed.Contains(r.JobId))
            .OrderBy(r => r.Seq)
            .ToList();

        int newNumber = (segments.Count > 0 ? segments[^1].Number : 0) + 1;
        string newPath = SegmentPath(newNumber);
        using (FileStream fresh = OpenAppend(newPath))
        {
            foreach (JournalRecord r in openRecords)
            {
                byte[] line = FrameLine(r);
                fresh.Write(line, 0, line.Length);
            }
            fresh.Flush(flushToDisk: true);
        }

        _active?.Dispose();

        // Reacquire the active handle BEFORE deleting the old segments: if this reopen fails with
        // _active already disposed and the old segments gone, every subsequent Append would fail
        // (JournalWriteFailed → all live jobs roll back, and rollback's own appends fail too) until
        // restart. Failing here instead leaves the old segments intact and _active restorable.
        FileStream newActive;
        try
        {
            newActive = OpenAppend(newPath);
        }
        catch (Exception reopenEx)
        {
            try
            {
                _active = OpenAppend(SegmentPath(_activeNumber));
                _logger.LogWarning(reopenEx, "Could not reopen the new journal segment after rotation; continuing on segment {Number}", _activeNumber);
            }
            catch (Exception restoreEx)
            {
                // Both handles are gone — the journal is wedged until the next successful Append
                // re-initializes or the process restarts. Loud, per the no-silent-failure directive.
                _logger.LogError(restoreEx, "Journal wedged: could not reopen either the new or the previous segment after rotation");
                _active = null;
                _initialized = false;   // force EnsureInitialized to retry from disk on the next Append
            }
            throw;
        }

        foreach ((int number, string path) in segments)
        {
            if (number < newNumber)
                TryDelete(path);
        }

        _activeNumber = newNumber;
        _active = newActive;
    }

    private void ReadSegment(string path, bool isLastSegment, List<JournalRecord> into)
    {
        if (!File.Exists(path))
            return;
        // Share Write+Delete: the active segment may be held open for append while we read it
        // (e.g. during rotation), so a plain File.ReadAllBytes would hit a sharing violation.
        byte[] bytes = ReadAllBytesShared(path);
        int start = 0;
        var lineRanges = new List<(int Start, int Length, bool Terminated)>();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lineRanges.Add((start, i - start, true));
                start = i + 1;
            }
        }
        if (start < bytes.Length)
            lineRanges.Add((start, bytes.Length - start, false));   // trailing bytes with no '\n' = possibly torn

        for (int li = 0; li < lineRanges.Count; li++)
        {
            (int lineStart, int lineLen, bool terminated) = lineRanges[li];
            if (lineLen == 0)
                continue;
            bool isTail = li == lineRanges.Count - 1;

            JournalRecord? record = TryParseLine(bytes.AsSpan(lineStart, lineLen), out string? failReason);
            if (record is not null)
            {
                into.Add(record);
                continue;
            }

            // A torn/failing tail line of the newest segment is expected (the guarded action either
            // did not happen or is resolved by recovery's filesystem probe) — discard silently.
            if (isTail && !terminated && isLastSegment)
                continue;

            // Any other bad line is a hardware-integrity event: log, skip, continue. The missing
            // record pushes that job toward recovery's conservative branch (§5.5).
            _logger.LogError("Journal integrity: bad record in {Path} (line {Line}): {Reason}", path, li, failReason);
        }
    }

    private static JournalRecord? TryParseLine(ReadOnlySpan<byte> line, out string? failReason)
    {
        if (!NdjsonFrame.TryDecode(line, out ReadOnlySpan<byte> json, out failReason))
            return null;
        try
        {
            JournalRecord? record = JsonSerializer.Deserialize(json, JournalJsonContext.Default.JournalRecord);
            if (record is null) { failReason = "null record"; return null; }
            return record;
        }
        catch (JsonException ex)
        {
            failReason = $"json: {ex.Message}";
            return null;
        }
    }

    private static byte[] FrameLine(JournalRecord record) =>
        NdjsonFrame.Encode(JsonSerializer.SerializeToUtf8Bytes(record, JournalJsonContext.Default.JournalRecord));

    private static byte[] ReadAllBytesShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[stream.Length];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        return offset == buffer.Length ? buffer : buffer[..offset];
    }

    private void EnsureDirectory() => Directory.CreateDirectory(_paths.JournalDirectory);

    private List<(int Number, string Path)> OrderedSegments()
    {
        var result = new List<(int, string)>();
        if (!Directory.Exists(_paths.JournalDirectory))
            return result;
        foreach (string path in Directory.EnumerateFiles(_paths.JournalDirectory, "journal-*.ndjsonl"))
        {
            string name = Path.GetFileNameWithoutExtension(path);   // journal-000001
            int dash = name.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(name.AsSpan(dash + 1), out int number))
                result.Add((number, path));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    private string SegmentPath(int number) =>
        Path.Combine(_paths.JournalDirectory, $"journal-{number:D6}.ndjsonl");

    private static FileStream OpenAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete old journal segment {Path}", path);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): a leftover segment is duplicate-tolerated by
            // recovery, so even an unexpected failure stays best-effort — but never silent.
            _logger.LogWarning(ex, "Could not delete old journal segment {Path} (unexpected)", path);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _active?.Dispose();
            _active = null;
        }
    }
}
