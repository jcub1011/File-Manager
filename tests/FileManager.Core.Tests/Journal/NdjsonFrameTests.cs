using System.Text;
using FileManager.Core.Journal;

namespace FileManager.Core.Tests.Journal;

/// <summary>Unit coverage for the `J1 &lt;crc:8-hex&gt; &lt;json&gt;\n` per-line framing (§5.5).
/// <see cref="NdjsonFrame"/> is <c>internal static</c>; the test project sees it via the
/// <c>InternalsVisibleTo("FileManager.Core.Tests")</c> in FileManager.Core.csproj.</summary>
public sealed class NdjsonFrameTests
{
    // Encode appends a trailing '\n'; a reader splits on '\n' and hands TryDecode the line WITHOUT it.
    private static bool DecodeFramed(byte[] framed, out byte[] json, out string? failReason)
    {
        bool ok = NdjsonFrame.TryDecode(framed.AsSpan(0, framed.Length - 1), out System.ReadOnlySpan<byte> payload, out failReason);
        json = payload.ToArray();
        return ok;
    }

    [Fact]
    public void Encode_then_TryDecode_round_trips_the_payload()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"t\":\"commit\",\"JobId\":\"abc\"}");
        byte[] framed = NdjsonFrame.Encode(json);

        Assert.Equal((byte)'\n', framed[^1]);
        Assert.Equal("J1 ", Encoding.ASCII.GetString(framed, 0, 3));

        Assert.True(DecodeFramed(framed, out byte[] decoded, out string? reason));
        Assert.Null(reason);
        Assert.Equal(json, decoded);
    }

    [Fact]
    public void Payload_containing_spaces_round_trips()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"path\":\"C:\\\\a b\\\\c d.txt\",\"note\":\"has spaces\"}");
        byte[] framed = NdjsonFrame.Encode(json);

        Assert.True(DecodeFramed(framed, out byte[] decoded, out string? reason));
        Assert.Null(reason);
        Assert.Equal(json, decoded);   // everything after the crc's space is payload, spaces included
    }

    [Fact]
    public void TryDecode_rejects_a_crc_mismatch()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        byte[] framed = NdjsonFrame.Encode(json);
        byte[] line = framed[..^1];   // drop the trailing '\n'
        line[12] ^= 0xFF;             // header is "J1 " (3) + 8 hex + " " (1) = 12; index 12 = payload[0]

        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("crc mismatch", reason);
    }

    [Fact]
    public void TryDecode_rejects_an_unknown_frame_version()
    {
        byte[] line = Encoding.ASCII.GetBytes("J2 00000000 {}");
        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("unknown frame version", reason);
    }

    [Fact]
    public void TryDecode_rejects_a_line_with_no_prefix_space()
    {
        byte[] line = Encoding.ASCII.GetBytes("J1nospacehere");
        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("no frame prefix", reason);
    }

    [Fact]
    public void TryDecode_rejects_a_missing_crc_payload_separator()
    {
        byte[] line = Encoding.ASCII.GetBytes("J1 abcd");   // only one space: no crc/payload split
        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("missing crc/payload", reason);
    }

    [Fact]
    public void TryDecode_rejects_a_non_hex_crc_field()
    {
        byte[] line = Encoding.ASCII.GetBytes("J1 zzzz {}");
        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("bad crc field", reason);
    }

    [Fact]
    public void TryDecode_rejects_an_over_long_crc_field()
    {
        byte[] line = Encoding.ASCII.GetBytes("J1 000000000 {}");   // 9 hex digits > 8
        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("bad crc field", reason);
    }

    [Fact]
    public void TryDecode_rejects_an_empty_crc_field()
    {
        byte[] line = Encoding.ASCII.GetBytes("J1  {}");   // two spaces => empty crc field
        Assert.False(NdjsonFrame.TryDecode(line, out _, out string? reason));
        Assert.Equal("bad crc field", reason);
    }
}
