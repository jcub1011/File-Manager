using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.Benchmarks.Fixtures;
using FileManager.Core.DryRun;
using FileManager.Core.IPC.Handlers;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>The per-entry allocation ladder for the dry-run wire conversion path — the benchmark form
/// of the claim recorded as "627 → 254 → 113 B per (file, op) pair" across Stages 1, 3 and 4 of the
/// peak-memory pass.
///
/// <para>Four tiers, run against the identical destination-sweep workload, so BenchmarkDotNet's
/// <c>Alloc Ratio</c> column attributes each stage on the reader's machine instead of citing a
/// document. Divide the <c>Allocated</c> column by <see cref="EntryPairs"/> to read bytes per pair
/// directly.</para>
///
/// <para>The first three tiers are the three numbers in the claim; the fourth goes past it. Measured
/// at 250,000 pairs on an i9-9900K under JIT, against the documented figures:</para>
/// <list type="table">
///   <item><term><see cref="LegacyStringConverter"/></term><description>627 B/pair — doc: 627</description></item>
///   <item><term><see cref="SpanProbe"/></term><description>252 B/pair — doc: 254 (Stage 1)</description></item>
///   <item><term><see cref="SpanProbeRecycled"/></term><description>108 B/pair — doc: 113 (Stage 3)</description></item>
///   <item><term><see cref="PooledCarriers"/></term><description>2.6 B/pair (Stage 4)</description></item>
/// </list>
///
/// <para>Note where the gate test sits in that ladder: <c>WireChunkConverterAllocationTests</c> feeds
/// PATH-STRING chunks, so its 113 B ceiling describes <see cref="SpanProbeRecycled"/>, not the
/// production sweep. The sweep's own carriers hand the converter a (directory, name) pair whose name
/// string came from the enumeration and is passed straight through to the wire, so the last
/// unconditional per-entry allocation disappears too — which is what
/// <see cref="PooledCarriers"/> shows and no existing test measures.</para>
///
/// <para><b>This measures CHURN, not retention.</b> The streamed pipeline keeps almost nothing alive;
/// every byte here is dead within a chunk. That is the point — in-run peak commit tracks total
/// uncollected churn, which is why removing per-entry garbage moves peak at all. It says nothing
/// about the service's settled footprint, which only <c>tools/FileManager.MemoryProbe</c> can
/// measure (BenchmarkDotNet runs under JIT with the host's runtimeconfig; the service is NativeAOT
/// with its GC knobs embedded at ILC time). See docs/dry-run-service-memory.md §5.5 and §13.</para>
///
/// <para>Companion, not replacement, to
/// <c>tests/FileManager.Core.Tests/DryRun/WireChunkConverterAllocationTests.cs</c>: that pins a
/// ceiling so a regression fails CI, this shows the delta the ceiling was derived from.</para></summary>
[MemoryDiagnoser]
public class WireChunkConverterBenchmarks
{
    /// <summary>Roughly one <c>DryRunEngine.WireChunkByteBudget</c> chunk at this path length — the
    /// unit the production converter is called with, and what makes the directory-table and memo hit
    /// rates realistic.</summary>
    private const int EntriesPerChunk = 300;

    private List<DryRunChunk> _stringChunks = null!;
    private List<DryRunChunk> _pooledChunks = null!;

    /// <summary>(file, op) pairs converted per invocation. 250k is the destination-heavy shape the
    /// peak pass was measured on.</summary>
    [Params(50_000, 250_000)]
    public int EntryPairs { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int chunkCount = (EntryPairs + EntriesPerChunk - 1) / EntriesPerChunk;
        // Both shapes are built once and reused: no tier mutates its input, and building inside the
        // measured region would swamp the very allocations being compared.
        _stringChunks = SweepShapes.StringPathChunks(chunkCount, EntriesPerChunk);
        _pooledChunks = SweepShapes.PooledCarrierChunks(chunkCount, EntriesPerChunk);
    }

    /// <summary>The pre-Stage-1 shape: a throwaway <c>Path.GetDirectoryName</c> string per file AND
    /// per op purely to probe the directory-table dictionary, a <c>Path.GetFileName</c> string each,
    /// a fresh wire record each, and fresh lists per chunk. Reimplemented here (the production code
    /// no longer contains it) as a frozen historical shape — it is the "627 B/pair" row and is not
    /// expected to track any future change to the real converter.</summary>
    [Benchmark(Baseline = true)]
    public int LegacyStringConverter()
    {
        LegacyConverter converter = new();
        int converted = 0;
        foreach (DryRunChunk chunk in _stringChunks)
            converted += Count(converter.Convert(chunk));
        return converted;
    }

    /// <summary>Stage 1 alone: the real converter with wire-DTO recycling switched off, fed
    /// path-string entries. Isolates the span directory probe and the reference memos — the
    /// throwaway strings are gone, the per-entry records are not.</summary>
    [Benchmark]
    public int SpanProbe()
    {
        DryRunStreamHandler.WireChunkConverter converter = new(recycleWireRecords: false);
        int converted = 0;
        foreach (DryRunChunk chunk in _stringChunks)
            converted += Count(converter.Convert(chunk));
        return converted;
    }

    /// <summary>Stages 1 + 3: the same converter with recycling on (the production default), so one
    /// chunk's worth of <c>DryRunFile</c>/<c>DryRunOperation</c> instances and their lists serve the
    /// whole stream. Legitimate here because this loop consumes each response before requesting the
    /// next, exactly as <c>IpcServer.ServeStreamAsync</c> does. What is left per entry is the one
    /// file-name string <c>Path.GetFileName</c> carves off the path — this is the shape the 113 B
    /// ceiling in <c>WireChunkConverterAllocationTests</c> was measured on.</summary>
    [Benchmark]
    public int SpanProbeRecycled()
    {
        DryRunStreamHandler.WireChunkConverter converter = new();
        int converted = 0;
        foreach (DryRunChunk chunk in _stringChunks)
            converted += Count(converter.Convert(chunk));
        return converted;
    }

    /// <summary>Stages 1 + 3 + 4 — the shipping destination-sweep path. Entries are pooled carriers
    /// holding (directory, name), so the converter takes its pair overload: no joined path exists
    /// anywhere, the directory instance is shared across 200 siblings and hits the memo, and the
    /// file-name string is passed through to the wire rather than re-derived. With the last
    /// unconditional per-entry allocation gone, what remains is amortized odds and ends — pool growth
    /// and the directory table's own entries, neither of which scales with entry count.</summary>
    [Benchmark]
    public int PooledCarriers()
    {
        DryRunStreamHandler.WireChunkConverter converter = new();
        int converted = 0;
        foreach (DryRunChunk chunk in _pooledChunks)
            converted += Count(converter.Convert(chunk));
        return converted;
    }

    /// <summary>Touches every produced list so no tier's conversion can be elided, and so a tier that
    /// silently produced nothing shows up as a wrong result rather than a fast one.</summary>
    private static int Count(DryRunChunkResponse response) =>
        response.Directories.Count + response.DestinationFiles.Count + response.DestinationOperations.Count;

    /// <summary>The converter as it stood before the span-probe pass — see
    /// <see cref="WireChunkConverterBenchmarks.LegacyStringConverter"/>. It shares the production
    /// <see cref="DryRunDirectoryTableBuilder"/> because the table itself was never the regression;
    /// what changed is how the directory is handed to it (a materialized string per entry, below,
    /// versus a span carved out of the path).</summary>
    private sealed class LegacyConverter
    {
        private readonly DryRunDirectoryTableBuilder _dirs = new();

        public DryRunChunkResponse Convert(DryRunChunk slice)
        {
            List<DryRunFile> files = new(slice.DestinationFiles.Count);
            foreach (IPhysicalFileView f in slice.DestinationFiles)
            {
                // The regression this shape carried: GetDirectoryName allocates a throwaway string
                // whose only purpose is to be hashed and discarded.
                string directory = Path.GetDirectoryName(f.Path)
                    ?? throw new ArgumentException($"'{f.Path}' is not an absolute file path");
                files.Add(new DryRunFile
                {
                    DirIndex = _dirs.GetOrAdd(directory),
                    FileName = Path.GetFileName(f.Path),
                    RootDirIndex = _dirs.GetOrAdd(f.Root),
                    Length = f.Length,
                    LastWritten = f.LastWritten,
                    IsReparsePoint = f.IsReparsePoint,
                });
            }

            List<DryRunOperation> ops = new(slice.DestinationOperations.Count);
            foreach (IFileOperationView o in slice.DestinationOperations)
            {
                // And again for the op, even though it describes the same path as its file — there
                // was no memo to notice.
                string directory = Path.GetDirectoryName(o.Path)
                    ?? throw new ArgumentException($"'{o.Path}' is not an absolute file path");
                ops.Add(new DryRunOperation
                {
                    DirIndex = _dirs.GetOrAdd(directory),
                    FileName = Path.GetFileName(o.Path),
                    RootDirIndex = _dirs.GetOrAdd(o.Root),
                    Kind = o.Kind,
                    SourceIndex = o.SourceIndex,
                    SubjectIndex = o.SubjectIndex,
                    SourceDisposition = o.SourceDisposition,
                    Detail = o.Detail,
                });
            }

            return new DryRunChunkResponse
            {
                Directories = _dirs.FlushNew(),
                DestinationFiles = files,
                DestinationOperations = ops,
            };
        }
    }
}
