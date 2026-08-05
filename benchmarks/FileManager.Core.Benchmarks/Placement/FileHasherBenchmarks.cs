using System.IO.Hashing;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using FileManager.Contracts.Profiles;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Benchmarks.Placement;

/// <summary>Measures <see cref="FileHasher"/> — the per-file streaming content hash on every job's
/// verify path, using the shipping default (XxHash128). Files live on real disk (as they do in
/// production); they are created once in setup so the measured method is pure hashing, not I/O
/// provisioning.</summary>
[MemoryDiagnoser]
public class FileHasherBenchmarks
{
    private readonly FileHasher _hasher = new(NullLogger<FileHasher>.Instance);
    private string _dir = null!;
    private string _file = null!;

    /// <summary>Spans the realistic range: a config-sized file, the hasher's 1 MiB buffer, and a
    /// large media file that exercises many buffer refills.</summary>
    [Params(4 * 1024, 1024 * 1024, 64 * 1024 * 1024)]
    public int FileSizeBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-bench-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "payload.bin");

        byte[] block = new byte[64 * 1024];
        for (int i = 0; i < block.Length; i++)
            block[i] = (byte)(i * 31 + 7);   // deterministic, non-zero content

        using FileStream fs = File.Create(_file);
        int remaining = FileSizeBytes;
        while (remaining > 0)
        {
            int chunk = Math.Min(block.Length, remaining);
            fs.Write(block, 0, chunk);
            remaining -= chunk;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp file; never fail a benchmark run over it.
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<string?> HashFile()
    {
        var result = await _hasher.HashFileAsync(_file, VerificationMethod.XxHash128);
        result.TryGetValue(out string? hash);
        return hash;
    }

    /// <summary>The bounded-read identity probe (spec §3.4.1) against the full read above. The ratio is the
    /// case for <see cref="LargeFileIdentity.SampledHash"/>: <see cref="HashFile"/> scales with file size
    /// while this reads a flat 8 MiB, so the two are near-identical at 4 KiB and 1 MiB (where the whole file
    /// is inside the window budget and the sampled path streams it anyway) and diverge from there. The 64
    /// MiB row is the one to read — and the real-world gap is larger still, since a duplicate pays this
    /// twice (source + destination) against two full reads.</summary>
    [Benchmark]
    public async Task<byte[]?> HashFileSampled()
    {
        var result = await _hasher.HashSampledToBytesAsync(_file, SampledHashLayout.Default);
        result.TryGetValue(out byte[]? digest);
        return digest;
    }
}

/// <summary>Head-to-head validation of the migration claim: streams the SAME file through SHA-256,
/// XxHash3 (64-bit) and XxHash128 (128-bit) using FileHasher's exact open flags + 1 MiB read loop,
/// so the ratios are apples-to-apples. Confirms (a) xxHash ≫ SHA-256 and (b) that the 64- and
/// 128-bit XXH3 variants run at essentially the same speed — i.e. the wider 128-bit digest is free.</summary>
[MemoryDiagnoser]
public class HashAlgorithmComparisonBenchmarks
{
    private const int BufferSize = 1024 * 1024;
    private string _dir = null!;
    private string _file = null!;

    [Params(4 * 1024, 1024 * 1024, 64 * 1024 * 1024)]
    public int FileSizeBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-bench-hashcmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "payload.bin");

        byte[] block = new byte[64 * 1024];
        for (int i = 0; i < block.Length; i++)
            block[i] = (byte)(i * 31 + 7);

        using FileStream fs = File.Create(_file);
        int remaining = FileSizeBytes;
        while (remaining > 0)
        {
            int chunk = Math.Min(block.Length, remaining);
            fs.Write(block, 0, chunk);
            remaining -= chunk;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private FileStream Open() => new(
        _file, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    [Benchmark(Baseline = true)]
    public async Task<byte[]> Sha256()
    {
        byte[] buffer = new byte[BufferSize];
        await using FileStream stream = Open();
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            hasher.AppendData(buffer, 0, read);
        return hasher.GetHashAndReset();
    }

    [Benchmark]
    public async Task<byte[]> XxHash3_64()
    {
        byte[] buffer = new byte[BufferSize];
        await using FileStream stream = Open();
        XxHash3 hasher = new();
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            hasher.Append(buffer.AsSpan(0, read));
        return hasher.GetHashAndReset();
    }

    [Benchmark]
    public async Task<byte[]> XxHash128_()
    {
        byte[] buffer = new byte[BufferSize];
        await using FileStream stream = Open();
        XxHash128 hasher = new();
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            hasher.Append(buffer.AsSpan(0, read));
        return hasher.GetHashAndReset();
    }
}

/// <summary>Picks the algorithm for the NDJSON journal/audit line checksum (§5.5 framing, `NdjsonFrame`)
/// by measuring every realistic 32-bit-output candidate on journal-line-sized in-memory buffers. The
/// use case is non-cryptographic corruption/truncation detection of short lines; the frame stores a
/// 32-bit checksum (8 hex), so all candidates keep the on-disk format drop-in. The winner drives the
/// §5.5 CRC-variant decision (resolved to CRC-32/IEEE — this suite is the evidence).
///
/// <para>Memory overhead has two parts. (1) <b>Per-op allocations</b> — the <c>Allocated</c> column below;
/// all candidates use the static <c>HashTo*</c> forms and should show ~0 B/op. (2) <b>Fixed static
/// footprint</b>, which MemoryDiagnoser does NOT capture (it is one-time, not per-op): <c>Crc32</c> and
/// <c>Crc32C</c> carry a ~1 KiB (256-entry) lookup table in software; <c>XxHash32</c>/<c>XxHash3</c> use
/// no table (small constant state); the <c>Crc32C_Hardware</c> path uses no table. All are tiny in
/// absolute terms — the real decision axis is throughput (<c>Mean</c>/<c>Ratio</c>) on short inputs,
/// where CRC-32C's large-buffer SSE4.2 advantage is not expected to materialize.</para></summary>
[MemoryDiagnoser]
public class ShortInputChecksumBenchmarks
{
    private byte[] _data = null!;

    [Params(64, 128, 256, 512, 1024)]
    public int SizeBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[SizeBytes];
        for (int i = 0; i < _data.Length; i++)
            _data[i] = (byte)(i * 31 + 7);
    }

    [Benchmark(Baseline = true)]
    public uint Crc32_() => Crc32.HashToUInt32(_data);

    /// <summary>CRC-32C (Castagnoli) via the SSE4.2 hardware instruction — the "should we switch" candidate.
    /// Not available as a type in System.IO.Hashing, hence hand-rolled. Returns 0 where SSE4.2 is absent
    /// (the row is then not representative on that host).</summary>
    [Benchmark]
    public uint Crc32C_Hardware() => Crc32CHardware(_data);

    [Benchmark]
    public uint XxHash32_() => XxHash32.HashToUInt32(_data);

    [Benchmark]
    public uint XxHash3_trunc32() => (uint)XxHash3.HashToUInt64(_data);

    /// <summary>Correct CRC-32C digest (init 0xFFFFFFFF, final xor-out) computed with the x86-64 SSE4.2
    /// CRC32 intrinsic: 8 bytes/step via the 64-bit form, byte-wise tail. The init/final are constant-time
    /// and do not affect the measured loop; they are included so this is a valid CRC-32C, not just a probe.</summary>
    private static uint Crc32CHardware(ReadOnlySpan<byte> data)
    {
        if (!Sse42.X64.IsSupported)
            return 0;

        ulong crc = 0xFFFFFFFFul;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
            crc = Sse42.X64.Crc32(crc, BitConverter.ToUInt64(data.Slice(i, 8)));

        uint crc32 = (uint)crc;
        for (; i < data.Length; i++)
            crc32 = Sse42.Crc32(crc32, data[i]);

        return crc32 ^ 0xFFFFFFFFu;
    }
}

/// <summary>Isolates the one tunable in <see cref="FileHasher"/>: the hard-coded 1 MiB read buffer.
/// Mirrors the hasher's exact open flags and streaming SHA-256 so the ratios say whether 1 MiB is
/// the right constant, or whether a smaller/larger buffer wins on this hardware.</summary>
[MemoryDiagnoser]
public class HashBufferSizeBenchmarks
{
    private const int FileSizeBytes = 64 * 1024 * 1024;
    private string _dir = null!;
    private string _file = null!;

    [Params(64 * 1024, 256 * 1024, 1024 * 1024, 4 * 1024 * 1024)]
    public int BufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-bench-hashbuf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "payload.bin");

        byte[] block = new byte[64 * 1024];
        Random.Shared.NextBytes(block);
        using FileStream fs = File.Create(_file);
        for (int written = 0; written < FileSizeBytes; written += block.Length)
            fs.Write(block, 0, block.Length);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Compare across BufferSize rows; 1 MiB is FileHasher's current constant.
    [Benchmark]
    public async Task<byte[]> Stream()
    {
        await using FileStream stream = new(
            _file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream);
    }
}
