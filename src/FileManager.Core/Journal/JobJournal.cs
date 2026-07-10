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
/// Framing per line: <c>J1 &lt;crc:8-hex&gt; &lt;json&gt;\n</c>. .NET's System.IO.Hashing ships
/// CRC-32 (IEEE), used here as the frame checksum; the doc names CRC-32C, but the exact
/// polynomial is not load-bearing — the same function guards write and read, giving the
/// torn-write detection §5.5 requires.</summary>
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

                if (_active.Length > _config.JournalRotateAtBytes)
                    RotateLocked();
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
        foreach ((int number, string path) in segments)
        {
            if (number < newNumber)
                TryDelete(path);
        }

        _activeNumber = newNumber;
        _active = OpenAppend(newPath);
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
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete old journal segment {Path}", path); }
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
