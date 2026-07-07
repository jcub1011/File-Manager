using FileManager.Contracts.IPC;
using System.Buffers.Binary;
using System.Text;

namespace FileManager.Contracts.Tests.IPC;

public sealed class IpcFrameCodecTests
{
    [Fact]
    public async Task RoundTrips_a_frame()
    {
        byte[] payload = Encoding.UTF8.GetBytes("""{"type":"get-status"}""");
        using MemoryStream stream = new();

        await IpcFrameCodec.WriteFrameAsync(stream, payload);
        stream.Position = 0;
        var read = await IpcFrameCodec.ReadFrameAsync(stream);

        Assert.True(read.TryGetValue(out byte[]? roundTripped));
        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public async Task Write_rejects_oversized_payload()
    {
        byte[] oversized = new byte[IpcFrameCodec.MaxPayloadBytes + 1];
        using MemoryStream stream = new();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => IpcFrameCodec.WriteFrameAsync(stream, oversized));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(IpcFrameCodec.MaxPayloadBytes + 1)]
    public async Task Read_rejects_out_of_range_lengths(int declaredLength)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        using MemoryStream stream = new(header);

        var read = await IpcFrameCodec.ReadFrameAsync(stream);

        Assert.True(read.TryGetError(out string? error));
        Assert.Contains("invalid frame length", error);
    }

    [Fact]
    public async Task Clean_eof_at_frame_boundary_is_a_distinct_failure()
    {
        using MemoryStream stream = new();
        var read = await IpcFrameCodec.ReadFrameAsync(stream);

        Assert.True(read.TryGetError(out string? error));
        Assert.Equal("connection closed", error);
    }

    [Fact]
    public async Task Truncated_header_is_a_failure()
    {
        using MemoryStream stream = new([0x08, 0x00]);
        var read = await IpcFrameCodec.ReadFrameAsync(stream);

        Assert.True(read.TryGetError(out string? error));
        Assert.Contains("mid-frame", error);
    }

    [Fact]
    public async Task Truncated_body_is_a_failure()
    {
        byte[] frame = new byte[4 + 3];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 10);   // promises 10, delivers 3
        using MemoryStream stream = new(frame);

        var read = await IpcFrameCodec.ReadFrameAsync(stream);

        Assert.True(read.TryGetError(out string? error));
        Assert.Contains("mid-frame", error);
    }

    [Fact]
    public async Task Unexpected_read_exception_is_a_traceable_failure_not_a_throw()
    {
        using ThrowingStream stream = new(new NotSupportedException("stream misbehaved"));

        var read = await IpcFrameCodec.ReadFrameAsync(stream);

        Assert.True(read.TryGetError(out string? error));
        Assert.Contains(nameof(NotSupportedException), error);
        Assert.Contains("stream misbehaved", error);
    }

    private sealed class ThrowingStream(Exception exception) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw exception;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
