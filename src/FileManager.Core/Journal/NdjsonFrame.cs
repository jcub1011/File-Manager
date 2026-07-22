using System;
using System.IO.Hashing;
using System.Text;

namespace FileManager.Core.Journal;

/// <summary>The `J1 &lt;crc:8-hex&gt; &lt;json&gt;\n` line framing shared by the write-ahead journal
/// (§5.5) and the disposition audit log (§7) — both append-only, fsync'd NDJSON with a per-line
/// checksum. The checksum is CRC-32 (IEEE) via .NET's System.IO.Hashing — the exact polynomial is
/// not load-bearing (the same function frames the write and validates the read), and CRC-32 was
/// chosen over CRC-32C on benchmark evidence (ShortInputChecksumBenchmarks): the gap is unobservable
/// behind the per-record fsync, and System.IO.Hashing has no Crc32C type to begin with.</summary>
internal static class NdjsonFrame
{
    private static ReadOnlySpan<byte> Prefix => "J1"u8;

    /// <summary>Builds a complete framed line (with trailing newline) around already-serialized JSON.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> json)
    {
        uint crc = Crc32.HashToUInt32(json);
        byte[] header = Encoding.ASCII.GetBytes($"J1 {crc:x8} ");
        byte[] line = new byte[header.Length + json.Length + 1];
        header.CopyTo(line, 0);
        json.CopyTo(line.AsSpan(header.Length));
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>Validates the prefix + CRC of one line and yields the raw JSON payload span.
    /// Returns false with a reason on a malformed or checksum-failing line.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> line, out ReadOnlySpan<byte> json, out string? failReason)
    {
        json = default;
        failReason = null;

        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace < 0) { failReason = "no frame prefix"; return false; }
        if (!line[..firstSpace].SequenceEqual(Prefix)) { failReason = "unknown frame version"; return false; }

        ReadOnlySpan<byte> rest = line[(firstSpace + 1)..];
        int secondSpace = rest.IndexOf((byte)' ');
        if (secondSpace < 0) { failReason = "missing crc/payload"; return false; }

        if (!TryParseHexU32(rest[..secondSpace], out uint expectedCrc)) { failReason = "bad crc field"; return false; }
        ReadOnlySpan<byte> payload = rest[(secondSpace + 1)..];
        if (Crc32.HashToUInt32(payload) != expectedCrc) { failReason = "crc mismatch"; return false; }

        json = payload;
        return true;
    }

    private static bool TryParseHexU32(ReadOnlySpan<byte> hex, out uint value)
    {
        value = 0;
        if (hex.Length is 0 or > 8)
            return false;
        foreach (byte b in hex)
        {
            int digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
                return false;
            value = (value << 4) | (uint)digit;
        }
        return true;
    }
}
