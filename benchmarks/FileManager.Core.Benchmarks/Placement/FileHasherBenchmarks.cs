using System.IO.Hashing;
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

    [Benchmark]
    public async Task<string?> HashFile()
    {
        var result = await _hasher.HashFileAsync(_file, VerificationMethod.XxHash128);
        result.TryGetValue(out string? hash);
        return hash;
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

/// <summary>Backs the "leave the journal CRC-32 alone" decision: hashes short in-memory buffers the
/// size of an NDJSON journal line with CRC-32 vs XxHash3. On tiny inputs CRC-32's hardware intrinsic
/// is expected to match or beat xxHash (whose advantage is throughput on large data), so migrating
/// the frame checksum would buy nothing.</summary>
[MemoryDiagnoser]
public class ShortInputChecksumBenchmarks
{
    private byte[] _data = null!;

    [Params(64, 256, 512)]
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

    [Benchmark]
    public ulong XxHash3_64() => XxHash3.HashToUInt64(_data);
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
