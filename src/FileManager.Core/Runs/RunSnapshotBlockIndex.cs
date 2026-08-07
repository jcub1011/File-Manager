using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Runs;

/// <summary>What turns a snapshot's display half from a tape into an array: a sidecar recording the byte
/// offset of every <see cref="BlockRows"/>th row, so "give me rows 4,000,000 to 4,000,512" is a seek
/// rather than a scan.
///
/// <para><b>Why it has to exist.</b> NDJSON is sequential-only. The preview could live with that while it
/// read the whole file once and kept every row — which is exactly the cost that made a large preview
/// expensive, and which stopped being bounded at all when the plan lost its 500,000-file cap. A windowed
/// view reads a page at a time instead, and a page needs a starting offset.</para>
///
/// <para><b>Sparse, not per row.</b> One offset per row would be 8 bytes a row — the same order as the
/// data itself — and would have to be paged in its own right. One per 512 rows is 16 KB per million
/// rows: small enough to hold whole in the UI, which is what keeps resolving a page to one seek plus a
/// forward scan of at most 511 rows.</para>
///
/// <para><b>Not durable, like the rest of the snapshot.</b> A missing or corrupt index costs a scan, not
/// correctness, and the whole run directory is deleted when the run closes. So it is written with the
/// same weak flush as the item files, and readers treat absence as "no index" rather than as an
/// error.</para></summary>
internal static class RunSnapshotBlockIndex
{
    /// <summary>Rows per indexed block. Matches <c>GetRunPlanStreamHandler.RowsPerChunk</c> deliberately:
    /// a page is served as whole blocks, and a block that did not line up with a frame would mean
    /// reading two of them to fill one.</summary>
    public const int BlockRows = 512;

    /// <summary>"FMBI" plus a layout version, so a stale sidecar is ignored rather than misread as
    /// offsets. The runs directory is swept at startup so this should never fire in practice; it costs
    /// 8 bytes and removes a whole class of "why is the preview showing garbage".</summary>
    private const long Magic = 0x_4642_4249_0000_0001L;

    /// <summary>Accumulates offsets while the writer streams rows past. Not thread-safe; the snapshot
    /// writer is single-threaded by construction.</summary>
    internal sealed class Builder
    {
        private readonly List<long> _offsets = [];

        /// <summary>Rows recorded so far — also the count a reader needs to size a virtual list.</summary>
        public long RowCount { get; private set; }

        /// <summary>Call once per row, with the stream position at which that row is ABOUT to be
        /// written. Records an offset on block boundaries only.
        /// <para>It must be the LOGICAL position (<c>FileStream.Position</c> on the buffered stream), not
        /// a flushed-to-disk figure: the reader seeks the finished file, where the two agree.</para></summary>
        public void Row(long positionBeforeWrite)
        {
            if (RowCount % BlockRows == 0)
                _offsets.Add(positionBeforeWrite);
            RowCount++;
        }

        /// <summary>Writes the sidecar. Returns null on success, or a message — a failed index is not a
        /// failed plan (the data file is intact and a scan still works), so the caller logs it rather
        /// than aborting the run.</summary>
        public string? Write(string path)
        {
            try
            {
                byte[] buffer = new byte[16 + (_offsets.Count * 8)];
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0), Magic);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), RowCount);
                for (int i = 0; i < _offsets.Count; i++)
                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16 + (i * 8)), _offsets[i]);
                File.WriteAllBytes(path, buffer);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>A loaded index.</summary>
    /// <param name="RowCount">Rows in the indexed file — what sizes a virtual list before a single row
    /// has been read.</param>
    /// <param name="Offsets">Byte offset of row <c>i * BlockRows</c>.</param>
    internal readonly record struct Index(long RowCount, long[] Offsets)
    {
        /// <summary>Where to start reading to reach <paramref name="ordinal"/>, and how many rows to skip
        /// forward from there (0..<see cref="BlockRows"/>-1). A negative skip means the ordinal is past
        /// the end of the file.</summary>
        public (long Offset, int Skip) Locate(long ordinal)
        {
            if (ordinal < 0 || ordinal >= RowCount)
                return (0, -1);
            long block = ordinal / BlockRows;
            return block >= Offsets.Length
                ? (0, -1)
                : (Offsets[block], (int)(ordinal % BlockRows));
        }
    }

    /// <summary>Loads a sidecar, or null when it is absent, truncated, or not ours. Null always means
    /// "fall back to a sequential read" — never an error to report.</summary>
    public static Index? TryRead(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 16 || (raw.Length - 16) % 8 != 0)
                return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(0)) != Magic)
                return null;
            long rows = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(8));
            if (rows < 0)
                return null;
            long[] offsets = new long[(raw.Length - 16) / 8];
            for (int i = 0; i < offsets.Length; i++)
                offsets[i] = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(16 + (i * 8)));
            return new Index(rows, offsets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Run snapshot: block index \"{Path}\" could not be read; falling back to a scan", path);
            return null;
        }
    }
}
