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

    /// <summary>The Failure message for a connection that closed cleanly at a frame boundary —
    /// the one read failure that is an expected end-of-stream, not a fault.</summary>
    public const string ConnectionClosedMessage = "connection closed";

    private const int HeaderBytes = 4;

    /// <summary>Writes one frame. An oversized payload is a programmer error and throws;
    /// transport failures are values, never exceptions. Cancellation is returned as
    /// <see cref="ResultStatus.Canceled"/>, never thrown.</summary>
    public static async Task<Result> WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayloadBytes, nameof(payload));

        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Canceled();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return $"write failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure) instead of faulting the connection task.
            return $"write failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
        }
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
                return ConnectionClosedMessage;
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
            return Result<byte[], string>.Canceled();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return $"read failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure) instead of faulting the connection task.
            return $"read failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
