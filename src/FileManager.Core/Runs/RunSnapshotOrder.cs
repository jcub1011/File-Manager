using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileManager.Core.Runs;

/// <summary>A display ORDER over a snapshot half: display position → row ordinal, as a flat
/// <c>int[]</c> on disk.
///
/// <para><b>Why an order file rather than sorted rows.</b> A destination row names its source by
/// ORDINAL — its position in <c>sources.ndjsonl</c> (<c>RunDestinationItem.SourceOrdinal</c>) — so
/// rewriting that file in sorted order would silently repoint every cross-reference in the plan. An
/// order file leaves the rows exactly as the plan wrote them and puts the ordering beside them. That
/// also means a second ordering — a different sort column, a search, a facet filter — is one more small
/// file rather than another copy of the rows.</para>
///
/// <para><b>Why it is built here and not in the UI.</b> The preview's sort used to happen client-side,
/// materializing one relative-path key per row: measured at ~147 MB at 500,000 rows, and linear beyond.
/// The keys are needed exactly once, to decide an order. Deciding it where the rows are already
/// streaming past costs no extra read, and what the client then holds is 4 bytes a row instead of a
/// string.</para>
///
/// <para><b>Bounded memory is the whole point,</b> so this is an external merge sort: entries accumulate
/// in runs of <see cref="RunRows"/>, each sorted and spilled, then merged with one entry resident per
/// run. Nothing scales with the total row count except the output file.</para></summary>
internal static class RunSnapshotOrder
{
    /// <summary>Rows per in-memory sort run. At ~120 bytes of key per row this is ~30 MB resident during
    /// phase 1 — high enough that an ordinary plan never spills at all (one run, sorted in memory,
    /// written straight out), and low enough that a 50-million-row plan peaks at the same figure.</summary>
    internal const int RunRows = 262_144;

    /// <summary>One row's sort identity. The root is an id into a small table rather than a string:
    /// there are as many distinct roots as the profile has sources, so interning them keeps an entry to
    /// 16 bytes plus the key it points at.
    /// <para><paramref name="Ordinal"/> is the row's position in its own file. For the destination half
    /// that is not yet the position the client will use — see <see cref="Builder.Write"/>.</para></summary>
    private readonly record struct Entry(string Key, int RootId, int Ordinal, bool SecondSegment);

    /// <summary>The comparator, and the ONLY definition of preview order.
    ///
    /// <para>It must agree exactly with what the two tabs computed client-side, because the Sources and
    /// Destinations lists are read side by side and a file has to sit at the same position in both:
    /// relative-path key, then root, then ordinal. The trailing ordinal tiebreak is what makes it total
    /// — <c>Array.Sort</c> is not stable, and rows equal on key and root would otherwise come out in an
    /// order that differed between the two halves.</para>
    ///
    /// <para><b>Do not "simplify" this to a full-path compare.</b> The two agree under a single root and
    /// diverge across several: a full path carries its root as a prefix, so it groups every row under
    /// one root before the next, while the relative key strips it and sorts the same relative file from
    /// two roots adjacently. That is the entire point — the Sources and Destinations tabs sit under
    /// different roots by construction (<c>C:\src\…</c> vs <c>D:\dst\…</c>), and full-path order would
    /// scatter a file away from its own destination row.
    /// <para>A separate trap, easily confused with this one and recorded at
    /// <c>docs/dry-run-service-memory.md:1033-1040</c>: <c>(directory, fileName)</c> TUPLE order is also
    /// not joined-path order, because <c>0x20 &lt; 0x5C</c> puts <c>C:\a b\c.txt</c> before
    /// <c>C:\a\z.txt</c>. This comparator sidesteps it by comparing the joined relative key as one
    /// string, exactly as the client did.</para></para></summary>
    private static int Compare(in Entry a, in Entry b, List<string> roots)
    {
        int c = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
        if (c != 0)
            return c;
        if (a.RootId != b.RootId)
        {
            c = string.Compare(roots[a.RootId], roots[b.RootId], StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
        }
        if (a.SecondSegment != b.SecondSegment)
            return a.SecondSegment ? 1 : -1;
        return a.Ordinal.CompareTo(b.Ordinal);
    }

    /// <summary>The sort key for one row: its path relative to its root, or the whole path when it has
    /// none. Mirrors the client-side <c>DryRunSort.RelativeKey</c> it replaces, including the "." case
    /// (a file sitting directly in the root keys on its bare name, because <c>Path.Join</c> would
    /// otherwise prepend ".\").</summary>
    public static string RelativeKey(string path, string? root, Dictionary<(string, string), string> relDirCache)
    {
        if (string.IsNullOrEmpty(root))
            return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileName(path);
        if (!relDirCache.TryGetValue((dir, root), out string? relDir))
            relDirCache[(dir, root)] = relDir = Path.GetRelativePath(root, dir);
        return relDir == "." ? name : Path.Join(relDir, name);
    }

    /// <summary>Accumulates rows, spilling sorted runs, and writes the finished order file. Fed one row
    /// at a time by the snapshot writer, in file order. Not thread-safe; the writer is
    /// single-threaded.</summary>
    internal sealed class Builder(string scratchDirectory, string label, ILogger logger) : IDisposable
    {
        private readonly List<string> _roots = [];
        private readonly Dictionary<string, int> _rootIds = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), string> _relDirCache = [];
        private readonly List<Entry> _pending = [];
        private readonly List<string> _runFiles = [];
        private int _first;
        private int _second;
        private bool _failed;

        /// <summary>Records a row of the FIRST segment (a source row, or a destination projection row).
        /// Its display ordinal is its position in its own file.</summary>
        public void Row(string path, string? root) => Add(path, root, _first++, secondSegment: false);

        /// <summary>Records a row of the SECOND segment — the delete half of the destination side.
        ///
        /// <para>The destination side the client sees is two files concatenated into one ordinal space:
        /// <c>destinations.ndjsonl</c> then <c>deletes.ndjsonl</c> (see
        /// <c>GetRunPlanStreamHandler</c>). A delete's final ordinal is therefore its own position PLUS
        /// the total projection count — which is not known until the plan ends, so it is applied when
        /// the file is written rather than here.</para></summary>
        public void SecondSegmentRow(string path, string? root) => Add(path, root, _second++, secondSegment: true);

        private void Add(string path, string? root, int ordinal, bool secondSegment)
        {
            if (_failed)
                return;
            _pending.Add(new Entry(RelativeKey(path, root, _relDirCache), RootId(root ?? ""), ordinal, secondSegment));
            if (_pending.Count >= RunRows)
                SpillRun();
        }

        private int RootId(string root)
        {
            if (_rootIds.TryGetValue(root, out int id))
                return id;
            id = _roots.Count;
            _roots.Add(root);
            _rootIds[root] = id;
            return id;
        }

        /// <summary>Sorts and spills the accumulated run. The relative-directory cache is deliberately
        /// NOT cleared: it is keyed by (directory, root), and a large tree revisits the same directories
        /// across run boundaries, which is the whole reason it exists.</summary>
        private void SpillRun()
        {
            if (_pending.Count == 0)
                return;
            _pending.Sort((a, b) => Compare(a, b, _roots));
            string path = Path.Combine(scratchDirectory, $"order-{label}-{_runFiles.Count:D5}.tmp");
            try
            {
                Directory.CreateDirectory(scratchDirectory);
                using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    foreach (Entry entry in _pending)
                        WriteEntry(stream, entry);
                }
                _runFiles.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A spill failure costs the ORDER, not the plan. Latching here rather than throwing
                // keeps the snapshot writer's own failure channel for things that make the work list
                // wrong; an unordered preview is a worse view of a correct plan.
                logger.LogWarning(ex, "Run snapshot: could not spill a sort run to \"{Path}\"", path);
                _failed = true;
            }
            _pending.Clear();
        }

        /// <summary>Writes the finished order file: the row count, then one <c>int</c> ordinal per
        /// display position. Returns null on success, or a message the caller LOGS — a missing order file
        /// means the preview shows plan order, never that the run failed.</summary>
        public string? Write(string orderPath)
        {
            try
            {
                if (_failed)
                    return "a sort run could not be spilled";
                if (_runFiles.Count == 0)
                {
                    // The common case by far: everything fitted in one in-memory run, so the scratch
                    // directory was never touched.
                    _pending.Sort((a, b) => Compare(a, b, _roots));
                    using FileStream single = Create(orderPath);
                    WriteHeader(single, _pending.Count);
                    byte[] slot = new byte[4];
                    foreach (Entry entry in _pending)
                        WriteOrdinal(single, slot, entry);
                    single.Flush(flushToDisk: false);
                    return null;
                }
                SpillRun();
                return _failed ? "a sort run could not be spilled" : Merge(orderPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
            finally
            {
                CleanUpRuns();
            }
        }

        /// <summary>K-way merge of the spilled runs. One entry resident per run, so memory here is
        /// proportional to the number of runs (a 50-million-row plan makes ~190) and not to rows.</summary>
        private string? Merge(string orderPath)
        {
            List<FileStream> streams = [];
            try
            {
                foreach (string file in _runFiles)
                    streams.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024));

                Entry?[] head = new Entry?[streams.Count];
                for (int i = 0; i < streams.Count; i++)
                    head[i] = ReadEntry(streams[i]);

                using FileStream output = Create(orderPath);
                WriteHeader(output, _first + _second);
                byte[] slot = new byte[4];
                while (true)
                {
                    int best = -1;
                    for (int i = 0; i < head.Length; i++)
                    {
                        if (head[i] is not { } candidate)
                            continue;
                        if (best < 0 || Compare(candidate, head[best]!.Value, _roots) < 0)
                            best = i;
                    }
                    if (best < 0)
                        break;
                    WriteOrdinal(output, slot, head[best]!.Value);
                    head[best] = ReadEntry(streams[best]);
                }
                output.Flush(flushToDisk: false);
                return null;
            }
            finally
            {
                foreach (FileStream stream in streams)
                    stream.Dispose();
            }
        }

        /// <summary>Applies the second segment's offset. A delete row's ordinal is its own position plus
        /// every projection row, which is what puts the two files in one index space for the client.</summary>
        private void WriteOrdinal(FileStream stream, byte[] slot, in Entry entry)
        {
            BinaryPrimitives.WriteInt32LittleEndian(slot, entry.SecondSegment ? _first + entry.Ordinal : entry.Ordinal);
            stream.Write(slot, 0, 4);
        }

        private static FileStream Create(string path) =>
            new(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);

        private static void WriteHeader(FileStream stream, long rows)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(header, rows);
            stream.Write(header);
        }

        private void CleanUpRuns()
        {
            foreach (string file in _runFiles)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Run snapshot: could not delete sort run \"{Path}\"", file);
                }
            }
            _runFiles.Clear();
        }

        private static void WriteEntry(FileStream stream, in Entry entry)
        {
            byte[] key = Encoding.UTF8.GetBytes(entry.Key);
            Span<byte> prefix = stackalloc byte[13];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, entry.Ordinal);
            BinaryPrimitives.WriteInt32LittleEndian(prefix[4..], entry.RootId);
            BinaryPrimitives.WriteInt32LittleEndian(prefix[8..], key.Length);
            prefix[12] = entry.SecondSegment ? (byte)1 : (byte)0;
            stream.Write(prefix);
            stream.Write(key, 0, key.Length);
        }

        private static Entry? ReadEntry(FileStream stream)
        {
            Span<byte> prefix = stackalloc byte[13];
            if (stream.ReadAtLeast(prefix, 13, throwOnEndOfStream: false) < 13)
                return null;
            int ordinal = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            int rootId = BinaryPrimitives.ReadInt32LittleEndian(prefix[4..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]);
            bool second = prefix[12] != 0;
            // A torn run file ends the merge rather than allocating on a bad length. The result is a
            // short order file, which the reader treats as a shorter view — never a crash.
            if (length < 0 || length > 64 * 1024)
                return null;
            byte[] key = new byte[length];
            if (stream.ReadAtLeast(key, length, throwOnEndOfStream: false) < length)
                return null;
            return new Entry(Encoding.UTF8.GetString(key), rootId, ordinal, second);
        }

        public void Dispose() => CleanUpRuns();
    }

    /// <summary>Rows the order file covers, or 0 when it is absent or unreadable. Zero always means
    /// "no order was built" — the caller falls back to plan order.</summary>
    public static long RowCount(string orderPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(orderPath))
                return 0;
            using FileStream stream = new(orderPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[8];
            return stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8
                ? 0
                : BinaryPrimitives.ReadInt64LittleEndian(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Run snapshot: order file \"{Path}\" could not be read", orderPath);
            return 0;
        }
    }

    /// <summary>The ordinals at display positions <paramref name="first"/>..+<paramref name="count"/>.
    /// Empty when the file is absent or the range starts past its end.</summary>
    public static int[] ReadRange(string orderPath, long first, int count, ILogger logger)
    {
        try
        {
            if (count <= 0 || first < 0 || !File.Exists(orderPath))
                return [];
            using FileStream stream = new(orderPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[8];
            if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
                return [];
            long total = BinaryPrimitives.ReadInt64LittleEndian(header);
            if (first >= total)
                return [];
            int take = (int)Math.Min(count, total - first);
            stream.Seek(8 + (first * 4), SeekOrigin.Begin);
            byte[] raw = new byte[take * 4];
            int read = stream.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false);
            int[] ordinals = new int[read / 4];
            for (int i = 0; i < ordinals.Length; i++)
                ordinals[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4));
            return ordinals;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Run snapshot: order file \"{Path}\" could not be read", orderPath);
            return [];
        }
    }
}
