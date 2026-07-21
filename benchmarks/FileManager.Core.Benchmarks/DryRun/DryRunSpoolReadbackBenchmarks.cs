using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using System.Text.Json;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>Isolates the dry-run spool <em>read-back</em> — the step entry-object pooling targets. On a
/// run that spills to disk, the engine reads every finding back from the snapshot and assembles it into
/// chunks; the naïve path deserializes a fresh <see cref="FileEvaluation"/> record graph per file
/// (source file + source op + dest files/ops + two lists + the record), a second full allocation set on
/// top of the one evaluation produced. Pooling replaces it with rented mutable carriers, recycled once
/// each chunk is consumed.
///
/// <para>This bench sidesteps the scan/hash/write cost (which would drown out the read-back signal) by
/// pre-framing the snapshot bytes once in setup, then measuring only the read-back loop:
/// <list type="bullet">
/// <item><see cref="Unpooled"/> — the pre-pooling behavior: <c>JsonSerializer.Deserialize</c> into a
/// fresh record graph per finding.</item>
/// <item><see cref="Pooled"/> — the current behavior: <see cref="PooledEvaluation.Parse"/> into rented
/// carriers, recycled every chunk.</item>
/// </list>
/// Both pay the same unavoidable per-string cost (paths/roots are real data the JSON reader must
/// materialize either way); the delta is exactly the per-file record/list/carrier churn pooling
/// removes. <see cref="MemoryDiagnoser"/> reports Allocated and the Gen0/1/2 collections.</para></summary>
[MemoryDiagnoser]
public class DryRunSpoolReadbackBenchmarks
{
    /// <summary>Entries recycled together — mirrors the engine recycling a chunk's carriers once its
    /// frame is serialized (I-POOL-RECYCLE), so the pool holds ~one chunk's working set at a time.</summary>
    private const int ChunkEntries = 4096;

    /// <summary>Findings read back. Sized to the regime pooling targets: a large scan that spilled.</summary>
    [Params(50_000, 250_000)]
    public int FileCount { get; set; }

    private byte[][] _framed = null!;

    [GlobalSetup]
    public void Setup()
    {
        _framed = new byte[FileCount][];
        for (int i = 0; i < FileCount; i++)
            _framed[i] = JsonSerializer.SerializeToUtf8Bytes(MakeEvaluation(i), DryRunSnapshotJsonContext.Default.FileEvaluation);
    }

    [Benchmark(Baseline = true)]
    public long Unpooled()
    {
        long acc = 0;
        foreach (byte[] bytes in _framed)
        {
            FileEvaluation e = JsonSerializer.Deserialize(bytes, DryRunSnapshotJsonContext.Default.FileEvaluation)!;
            acc += e.SourceFile.Length + e.DestinationOps.Count;   // consume so nothing is elided
        }
        return acc;
    }

    [Benchmark]
    public long Pooled()
    {
        EvaluationCarrierPool pool = new();
        List<PooledEvaluation> chunk = new(ChunkEntries);
        long acc = 0;
        foreach (byte[] bytes in _framed)
        {
            PooledEvaluation e = PooledEvaluation.Parse(bytes, pool);
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

    /// <summary>A representative finding: a deep-ish source path, one pre-existing target it would
    /// overwrite (so there is a destination file + a SubjectIndex to carry), realistic string lengths.</summary>
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
