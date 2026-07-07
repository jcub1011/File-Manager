using FileManager.Contracts.Primitives;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

/// <summary>Length-prefixed framing (spec §2.1): a 4-byte little-endian payload length followed
/// by that many bytes of UTF-8 JSON. One frame = one serialized IpcRequest, IpcResponse, or
/// EngineEvent. Used by both the client (here) and the server (Core).</summary>
public static class IpcFrameCodec
{
    /// <summary>Sanity cap on a single frame's payload (architecture §3.1).</summary>
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    private const int HeaderBytes = 4;

    /// <summary>Writes one frame. An oversized payload is a programmer error and throws;
    /// transport failures surface as the stream's own exceptions (callers own the connection).</summary>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayloadBytes, nameof(payload));

        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Expected transport failures are values, never exceptions:
    /// a connection closed cleanly at a frame boundary, a truncated frame, and an out-of-range
    /// length all return Failure with a distinguishing message.</summary>
    public static async Task<Result<byte[], string>> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderBytes];
        try
        {
            // First byte read separately so clean EOF at a frame boundary is distinguishable
            // from a frame torn mid-header.
            int first = await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (first == 0)
                return "connection closed";
            await stream.ReadExactlyAsync(header.AsMemory(1, HeaderBytes - 1), ct).ConfigureAwait(false);

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxPayloadBytes)
                return $"invalid frame length {length}";

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            return payload;
        }
        catch (EndOfStreamException)
        {
            return "connection closed mid-frame";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return $"read failed: {ex.Message}";
        }
    }
}
