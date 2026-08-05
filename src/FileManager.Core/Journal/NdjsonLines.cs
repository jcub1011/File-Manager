using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Journal;

/// <summary>Splitting newline-delimited byte content into lines, without decoding it to text first.
///
/// <para><b>Why bytes.</b> Every consumer of an NDJSON line here immediately hands it to
/// <see cref="NdjsonFrame"/> and then to a UTF-8 JSON deserializer, both of which want bytes. Decoding a
/// line to a <c>string</c> and re-encoding it is two full transcodes and two allocations per record to
/// undo work the file already had in the right form — which on a 500,000-file run snapshot, read in full
/// on every plan replay, is millions of throwaway objects.</para>
///
/// <para>Both helpers are index-based rather than span-based on purpose: their callers are iterator
/// methods (<c>yield return</c>), and a <c>ref struct</c> cannot live across a yield.</para></summary>
internal static class NdjsonLines
{
    /// <summary>Enumerates each line in <paramref name="bytes"/> as an (offset, length) pair, skipping
    /// empty lines. A trailing line with no newline is still yielded.</summary>
    public static IEnumerable<(int Offset, int Length)> Spans(byte[] bytes, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        for (int i = start; i <= bytes.Length; i++)
        {
            if (i != bytes.Length && bytes[i] != (byte)'\n')
                continue;
            int length = TrimCarriageReturn(bytes, start, i - start);
            if (length > 0)
                yield return (start, length);
            start = i + 1;
        }
    }

    /// <summary>Drops a trailing CR from a line, so a CRLF-terminated file reads the same as an
    /// LF-terminated one.
    /// <para>Not defensive padding. These files are written with bare LF, but anything that rewrites one
    /// through a text API on Windows re-terminates every line with CRLF — and the CR then travels into the
    /// framed payload, where it fails the CRC. The result is not a skipped line but a file that reads back
    /// as EMPTY, which for a delete list reads as "no orphans" rather than as damage.</para></summary>
    private static int TrimCarriageReturn(byte[] bytes, int offset, int length) =>
        length > 0 && bytes[offset + length - 1] == (byte)'\r' ? length - 1 : length;

    /// <summary>Reads at most <paramref name="maxTailBytes"/> from the END of a file, starting at a line
    /// boundary — the shape a "most recent N records" read wants from an append-only file that is never
    /// truncated. <paramref name="start"/> is where the first COMPLETE line begins: a tail read almost
    /// always lands mid-record, and that partial first line has to be dropped rather than parsed.</summary>
    public static byte[] ReadTail(string path, long maxTailBytes, out int start)
    {
        using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length <= maxTailBytes)
        {
            byte[] whole = new byte[(int)length];
            stream.ReadExactly(whole);
            start = 0;
            return whole;
        }
        stream.Seek(length - maxTailBytes, SeekOrigin.Begin);
        byte[] tail = new byte[(int)maxTailBytes];
        stream.ReadExactly(tail);
        int newline = Array.IndexOf(tail, (byte)'\n');
        start = newline >= 0 ? newline + 1 : tail.Length;
        return tail;
    }

    /// <summary>Streams a whole file's lines as byte arrays, reading through a pooled buffer so the file
    /// is never held in memory at once.
    ///
    /// <para>Each yielded array is a fresh copy of one line, because the caller may hold it across the
    /// yield while the shared buffer moves on. That is one allocation per line — against the two (a
    /// decoded string plus a re-encoded array) that reading this as text cost, and it is the line's own
    /// bytes rather than a transcode of them.</para>
    ///
    /// <para>A line longer than the buffer grows it, so a single enormous record is read correctly rather
    /// than split into fragments that would each fail their checksum.</para></summary>
    public static IEnumerable<byte[]> ReadAllLines(string path, int bufferSize = 64 * 1024)
    {
        using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            // Bytes of the current, not-yet-terminated line, always at buffer[0..held].
            int held = 0;
            while (true)
            {
                int read = stream.Read(buffer, held, buffer.Length - held);
                if (read == 0)
                    break;
                int scanned = held;
                held += read;
                int consumed = 0;
                for (int i = scanned; i < held; i++)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;
                    int length = TrimCarriageReturn(buffer, consumed, i - consumed);
                    if (length > 0)
                        yield return buffer[consumed..(consumed + length)];
                    consumed = i + 1;
                }
                // Carry the unterminated remainder to the front and keep filling behind it.
                held -= consumed;
                if (held > 0 && consumed > 0)
                    Array.Copy(buffer, consumed, buffer, 0, held);
                if (held == buffer.Length)
                {
                    // The line is longer than the buffer: grow rather than emit a fragment, which would
                    // fail its frame checksum and be reported as a corrupt record.
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Array.Copy(buffer, bigger, held);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
            }
            int trailing = TrimCarriageReturn(buffer, 0, held);
            if (trailing > 0)
                yield return buffer[..trailing];   // a final line with no trailing newline
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
