using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using System.Buffers;
using System.Text.Json;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>Isolates the dry-run spool <em>read-back</em> — the step entry-object pooling and the
/// trimmed snapshot format target. On a run that spills to disk, the engine reads every finding back
/// from the snapshot and assembles it into chunks. The bench sidesteps the scan/hash/write cost (which
/// would drown out the read-back signal) by pre-framing the snapshot bytes once in setup, then
/// measuring only the read-back loop, across three tiers:
/// <list type="bullet">
/// <item><see cref="Unpooled"/> — the original behavior: full-record JSON, <c>JsonSerializer.Deserialize</c>
/// into a fresh <see cref="FileEvaluation"/> graph per finding.</item>
/// <item><see cref="PooledV1"/> — pooling only: the same full-record JSON, parsed into recycled
/// carriers (no format trim, no string dedup).</item>
/// <item><see cref="PooledV2"/> — pooling + the trimmed format: <see cref="DryRunSnapshotFormat"/>'s
/// shape (omitted derivable Path/Root, interned roots), parsed into recycled carriers. This is the
/// current production path.</item>
/// </list>
/// The V1→V2 delta is the payoff of optimizations A (root interning), B (dest-op path reuse) and C
/// (omit the source op's path/root); the Unpooled→V1 delta is the earlier pooling win.
/// <see cref="MemoryDiagnoser"/> reports Allocated and the GC collections.</summary>
[MemoryDiagnoser]
public class DryRunSpoolReadbackBenchmarks
{
    /// <summary>Entries recycled together — mirrors the engine recycling a chunk's carriers once its
    /// frame is serialized (I-POOL-RECYCLE), so the pool holds ~one chunk's working set at a time.</summary>
    private const int ChunkEntries = 4096;

    /// <summary>Findings read back. Sized to the regime these optimizations target: a large scan that
    /// spilled.</summary>
    [Params(50_000, 250_000)]
    public int FileCount { get; set; }

    private byte[][] _fullFramed = null!;      // naïve full-record JSON (Unpooled + PooledV1)
    private byte[][] _trimmedFramed = null!;   // DryRunSnapshotFormat's trimmed shape (PooledV2)

    [GlobalSetup]
    public void Setup()
    {
        _fullFramed = new byte[FileCount][];
        _trimmedFramed = new byte[FileCount][];
        ArrayBufferWriter<byte> buffer = new();
        Utf8JsonWriter writer = new(buffer);
        for (int i = 0; i < FileCount; i++)
        {
            FileEvaluation e = MakeEvaluation(i);
            _fullFramed[i] = JsonSerializer.SerializeToUtf8Bytes(e, DryRunSnapshotJsonContext.Default.FileEvaluation);

            buffer.Clear();
            writer.Reset(buffer);
            DryRunSnapshotFormat.Write(writer, e);
            writer.Flush();
            _trimmedFramed[i] = buffer.WrittenSpan.ToArray();
        }
        writer.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long Unpooled()
    {
        long acc = 0;
        foreach (byte[] bytes in _fullFramed)
        {
            FileEvaluation e = JsonSerializer.Deserialize(bytes, DryRunSnapshotJsonContext.Default.FileEvaluation)!;
            acc += e.SourceFile.Length + e.DestinationOps.Count;   // consume so nothing is elided
        }
        return acc;
    }

    [Benchmark]
    public long PooledV1() => ReadAll(_fullFramed, static (b, p) => ParseFull(b, p));

    [Benchmark]
    public long PooledV2() => ReadAll(_trimmedFramed, static (b, p) => DryRunSnapshotFormat.Read(b, p));

    private static long ReadAll(byte[][] framed, Func<byte[], EvaluationCarrierPool, PooledEvaluation> parse)
    {
        EvaluationCarrierPool pool = new();
        List<PooledEvaluation> chunk = new(ChunkEntries);
        long acc = 0;
        foreach (byte[] bytes in framed)
        {
            PooledEvaluation e = parse(bytes, pool);
            acc += e.SourceFile.Length + e.DestinationOps.Count;
            chunk.Add(e);
            if (chunk.Count == ChunkEntries)
            {
                foreach (PooledEvaluation c in chunk) c.Recycle();
                chunk.Clear();
            }
        }
        foreach (PooledEvaluation c in chunk) c.Recycle();
        return acc;
    }

    /// <summary>The pre-trim carrier parser: reads the full record shape (every Path/Root present, no
    /// interning) into rented carriers. Mirrors the read path as it stood after pooling but before the
    /// trimmed format — the PooledV1 tier.</summary>
    private static PooledEvaluation ParseFull(byte[] json, EvaluationCarrierPool pool)
    {
        PooledEvaluation e = pool.RentEvaluation();
        Utf8JsonReader reader = new(json);
        reader.Read();   // StartObject
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("SourceFile"u8)) { reader.Read(); ReadFileFull(ref reader, e.SourceFile); }
            else if (reader.ValueTextEquals("SourceOp"u8)) { reader.Read(); ReadOpFull(ref reader, e.SourceOp); }
            else if (reader.ValueTextEquals("DestinationFiles"u8))
            {
                reader.Read();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    PooledPhysicalFile f = pool.RentFile();
                    ReadFileFull(ref reader, f);
                    e.DestinationFiles.Add(f);
                }
            }
            else if (reader.ValueTextEquals("DestinationOps"u8))
            {
                reader.Read();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    PooledFileOperation o = pool.RentOp();
                    ReadOpFull(ref reader, o);
                    e.DestinationOps.Add(o);
                }
            }
            else { reader.Read(); reader.Skip(); }
        }
        return e;
    }

    private static void ReadFileFull(ref Utf8JsonReader reader, PooledPhysicalFile f)
    {
        f.Length = 0; f.IsReparsePoint = false; f.LastWritten = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Path"u8)) { reader.Read(); f.Path = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Root"u8)) { reader.Read(); f.Root = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Length"u8)) { reader.Read(); f.Length = reader.GetInt64(); }
            else if (reader.ValueTextEquals("LastWritten"u8)) { reader.Read(); f.LastWritten = reader.GetDateTimeOffset(); }
            else if (reader.ValueTextEquals("IsReparsePoint"u8)) { reader.Read(); f.IsReparsePoint = reader.GetBoolean(); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    private static void ReadOpFull(ref Utf8JsonReader reader, PooledFileOperation o)
    {
        o.SourceIndex = -1; o.SubjectIndex = -1; o.SourceDisposition = null; o.Detail = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Path"u8)) { reader.Read(); o.Path = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Root"u8)) { reader.Read(); o.Root = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Kind"u8)) { reader.Read(); o.Kind = (OperationKind)reader.GetInt32(); }
            else if (reader.ValueTextEquals("SourceIndex"u8)) { reader.Read(); o.SourceIndex = reader.GetInt32(); }
            else if (reader.ValueTextEquals("SubjectIndex"u8)) { reader.Read(); o.SubjectIndex = reader.GetInt32(); }
            else if (reader.ValueTextEquals("SourceDisposition"u8)) { reader.Read(); if (reader.TokenType != JsonTokenType.Null) o.SourceDisposition = (OnSuccessAction)reader.GetInt32(); }
            else if (reader.ValueTextEquals("Detail"u8)) { reader.Read(); o.Detail = reader.GetString(); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    /// <summary>A representative finding: a deep-ish source path, one pre-existing target it would
    /// overwrite (so there is a destination file, a SubjectIndex, and a derivable op path — exercising
    /// the trim), realistic string lengths.</summary>
    private static FileEvaluation MakeEvaluation(int i)
    {
        string sourceRoot = @"C:\Users\example\Pictures\import";
        string sourcePath = $@"{sourceRoot}\2026\event-{i / 1000:D3}\IMG_{i:D6}.jpg";
        string targetRoot = @"D:\backup\media-archive\projects";
        string targetPath = $@"{targetRoot}\2026\event-{i / 1000:D3}\IMG_{i:D6}.jpg";
        DateTimeOffset when = DateTimeOffset.UnixEpoch.AddSeconds(i);

        PhysicalFile source = new()
        {
            Path = sourcePath, Root = sourceRoot, Length = 4_000_000 + i, LastWritten = when, IsReparsePoint = false,
        };
        VirtualFileOperation sourceOp = new()
        {
            Path = sourcePath, Root = sourceRoot, Kind = OperationKind.Processed, SourceDisposition = OnSuccessAction.KeepSource,
        };
        PhysicalFile existing = new()
        {
            Path = targetPath, Root = targetRoot, Length = 3_900_000 + i, LastWritten = when, IsReparsePoint = false,
        };
        VirtualFileOperation destOp = new()
        {
            Path = targetPath, Root = targetRoot, Kind = OperationKind.Overwrite, SourceIndex = 0, SubjectIndex = 0,
            Detail = "existing file last modified 2026-01-01 00:00:00 UTC",
        };
        return new FileEvaluation(source, sourceOp, [existing], [destOp]);
    }
}
