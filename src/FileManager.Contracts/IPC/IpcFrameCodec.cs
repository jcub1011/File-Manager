using FileManager.Contracts.Primitives;
using System;
using System.Buffers;
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

    /// <summary>Size of the length prefix. Public so a caller writing many frames can hold one
    /// correctly-sized scratch header (see the <c>headerBuffer</c> overload of
    /// <see cref="WriteFrameAsync(Stream, ReadOnlyMemory{byte}, byte[], CancellationToken)"/>).</summary>
    public const int HeaderBytes = 4;

    /// <summary>Writes one frame, allocating its 4-byte length prefix. An oversized payload is a
    /// programmer error and throws; transport failures are values, never exceptions. Cancellation is
    /// returned as <see cref="ResultStatus.Canceled"/>, never thrown.</summary>
    public static Task<Result> WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
        WriteFrameAsync(stream, payload, new byte[HeaderBytes], ct);

    /// <summary>Writes one frame using a caller-supplied header buffer, so a connection emitting
    /// thousands of frames does not allocate one 4-byte array per frame.
    ///
    /// <para><paramref name="headerBuffer"/> must be exactly <see cref="HeaderBytes"/> long and must
    /// not be shared across concurrent writers — this method is async, so a static or cross-connection
    /// buffer would be torn by interleaved writes. One buffer per connection is the intended
    /// lifetime.</para></summary>
    public static async Task<Result> WriteFrameAsync(
        Stream stream, ReadOnlyMemory<byte> payload, byte[] headerBuffer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(headerBuffer);
        ArgumentOutOfRangeException.ThrowIfNotEqual(headerBuffer.Length, HeaderBytes, nameof(headerBuffer));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayloadBytes, nameof(payload));

        byte[] header = headerBuffer;
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

    /// <summary>Reads one frame into a caller-owned buffer instead of a fresh exact-size <c>byte[]</c>,
    /// returning the payload length; the payload is <c>destination.WrittenSpan</c>. The read-side mirror
    /// of <c>IpcSerializer.SerializeResponse(IpcResponse, IBufferWriter&lt;byte&gt;)</c>, and for the same
    /// reason.
    ///
    /// <para>Exists for the client's streamed dry-run loop, where one run delivers many multi-megabyte
    /// chunk frames. Measured: a 20,000-file chunk serializes to a <strong>7.4 MB</strong> frame — every
    /// frame the array overload returns is therefore a Large Object Heap allocation, and the LOH is
    /// uncompacted by default and only collected on a gen2, so the churn becomes lasting committed memory
    /// rather than a transient read. A run at the streamed cap allocated <strong>133 bytes per wire
    /// record</strong> in frame buffers alone. A caller that <c>Clear()</c>s and reuses one
    /// <see cref="ArrayBufferWriter{T}"/> turns all of it into one buffer that grows once.</para>
    ///
    /// <para><c>docs/dry-run-service-memory.md</c> §11 rejected pooling this path "for no
    /// <em>service</em>-side benefit" — correct, and scoped to the service: the server writes frames and
    /// already pools that side. This is the consumer that benefits, and it is added alongside the array
    /// overload rather than changing it, so no existing caller moves.</para>
    ///
    /// <para>Failure semantics are identical to the array overload. On failure nothing is written.</para></summary>
    public static async Task<Result<int, string>> ReadFrameAsync(
        Stream stream, ArrayBufferWriter<byte> destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(destination);

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

            // GetMemory may hand back more than requested; Advance commits exactly the frame's length so
            // WrittenSpan is the payload and nothing else.
            Memory<byte> destinationMemory = destination.GetMemory(length)[..length];
            await stream.ReadExactlyAsync(destinationMemory, ct).ConfigureAwait(false);
            destination.Advance(length);
            return length;
        }
        catch (EndOfStreamException)
        {
            return "connection closed mid-frame";
        }
        catch (OperationCanceledException)
        {
            return Result<int, string>.Canceled();
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
