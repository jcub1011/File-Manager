using System;
using System.Buffers;
using System.IO;

namespace FileManager.Core.Runs;

/// <summary>Reads newline-delimited records forward from wherever a stream is currently positioned,
/// without reading the file before that point.
///
/// <para><b>Why not <c>NdjsonLines.ReadAllLines</c>.</b> That reads the whole file, which is exactly
/// right for the consumers that want every row (execution enqueues the whole copy list) and exactly
/// wrong for a page: the preview asks for 512 rows out of millions, having already seeked to the block
/// they live in. Reading from byte zero to get there would make a windowed read slower than the
/// whole-file read it exists to replace.</para>
///
/// <para>Bytes, not text, for the same reason the whole-file reader is: the caller hands each line
/// straight to <c>NdjsonFrame</c> and a UTF-8 deserializer, so decoding to a <c>string</c> and
/// re-encoding would be two transcodes per row to undo work the file already had in the right
/// form.</para>
///
/// <para>Rented buffer, returned on dispose — a page read happens on every scroll, so a fresh 64 KB
/// array per page would be steady LOH-adjacent churn for the life of a preview. The reader does NOT own
/// the stream: the caller reuses one open handle across several blocks.</para></summary>
internal sealed class StreamLineReader(Stream stream) : IDisposable
{
    private const int InitialBuffer = 64 * 1024;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBuffer);
    private int _start;   // first unconsumed byte
    private int _end;     // one past the last byte read from the stream
    private bool _exhausted;

    /// <summary>The next line, without its terminator. False at end of stream. The span is valid only
    /// until the next call — it points into the reader's own buffer, so a caller that keeps a line must
    /// copy it.
    /// <para>An out-parameter rather than a nullable return because a span cannot be nullable, and
    /// "empty" is not the same as "no more": an empty line is skipped, end of stream is not.</para></summary>
    public bool TryNextLine(out ReadOnlySpan<byte> line)
    {
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                int length = Trim(newline - _start);
                int lineStart = _start;
                _start = newline + 1;
                // An empty line is skipped rather than yielded, matching NdjsonLines: a file that picked
                // up a stray blank must read as the rows it has, not as one unreadable row.
                if (length == 0)
                    continue;
                line = _buffer.AsSpan(lineStart, length);
                return true;
            }
            if (_exhausted)
            {
                // A trailing line with no newline is still a line.
                int length = Trim(_end - _start);
                int lineStart = _start;
                _start = _end;
                line = length == 0 ? default : _buffer.AsSpan(lineStart, length);
                return length != 0;
            }
            Fill();
        }
    }

    /// <summary>Drops a trailing CR so a CRLF-terminated file reads the same as an LF-terminated one —
    /// the same hazard <c>NdjsonLines</c> documents: a CR that reaches the framed payload fails the CRC,
    /// and the file then reads as EMPTY rather than as damaged.</summary>
    private int Trim(int length) =>
        length > 0 && _buffer[_start + length - 1] == (byte)'\r' ? length - 1 : length;

    /// <summary>Compacts what is unconsumed to the front and reads more, growing only when a single line
    /// does not fit. Growth is the pathological case (a row longer than 64 KB), not the normal one.</summary>
    private void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
        int read = stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read <= 0)
            _exhausted = true;
        else
            _end += read;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0)
            return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
