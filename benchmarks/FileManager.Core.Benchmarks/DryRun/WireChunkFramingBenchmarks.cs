using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.Benchmarks.Fixtures;
using FileManager.Core.DryRun;
using FileManager.Core.IPC.Handlers;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>What the chunk byte budget costs — the benchmark form of the Large Object Heap claim
/// behind <c>DryRunEngine.WireChunkByteBudget</c> going from 1 MiB to 48 KiB.
///
/// <para>The measured before/after recorded in docs/dry-run-service-memory.md §6 is: largest frame
/// 1,446,714 B → 27,846 B, frames 8 → 289, frames on the LOH 5 → 0. The reasoning is that "the
/// binding constraint is NOT the 16 MiB protocol cap; it is the 85,000-byte Large Object Heap
/// threshold. Each response is serialized into a fresh exact-size <c>byte[]</c>, so any frame at or
/// above that lands on the LOH — which is not compacted by default and only collected with a gen2,
/// i.e. it is a lasting addition to the process's committed footprint rather than a transient
/// write."</para>
///
/// <para><b>Where to read the claim.</b> Two places, because no single BenchmarkDotNet column shows
/// it:</para>
/// <list type="bullet">
///   <item>The <c>frames:</c> line this class prints from <see cref="Setup"/> — frame count, largest
///   frame, and how many landed at or above the LOH threshold, for the current parameter
///   combination. That is the direct analogue of the doc's table.</item>
///   <item>The <b>Gen2</b> column of the run itself. The 1 MiB tier allocates large arrays that only
///   a gen2 reclaims; the 48 KiB tier should show none.</item>
/// </list>
///
/// <para>Chunking uses the production rule — <c>DryRunEngine.WireFileUpperBoundBytes</c> /
/// <c>WireOpUpperBoundBytes</c>, the same estimator <c>DestinationProjector</c>'s producer packs
/// with — rather than a reimplementation, so the frames here are the frames the service emits.
/// Complements <c>tests/FileManager.Core.Tests/DryRun/DryRunFrameSizeTests.cs</c>, which asserts a
/// property of the chunking constants but measures no cost.</para></summary>
[MemoryDiagnoser]
public class WireChunkFramingBenchmarks
{
    /// <summary>Arrays from this size up are allocated on the Large Object Heap.</summary>
    private const int LargeObjectHeapThreshold = 85_000;

    private List<IPhysicalFileView> _files = null!;
    private List<IFileOperationView> _ops = null!;
    /// <summary>Parallel to <see cref="_files"/>. The producer packs from the name it already holds
    /// (its carriers deliberately never materialize a path to take one from), so the benchmark holds
    /// the names the same way rather than reading them back off the carriers.</summary>
    private string[] _names = null!;

    /// <summary>1 MiB is the retired budget, 48 KiB the current one
    /// (<c>DryRunEngine.WireChunkByteBudget</c>).</summary>
    [Params(48 * 1024, 1024 * 1024)]
    public int ChunkByteBudget { get; set; }

    [Params(50_000, 250_000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _files = new List<IPhysicalFileView>(EntryCount);
        _ops = new List<IFileOperationView>(EntryCount);
        _names = new string[EntryCount];
        string directory = SweepShapes.Directory(0);
        for (int i = 0; i < EntryCount; i++)
        {
            if (i % SweepShapes.FilesPerDirectory == 0)
                directory = SweepShapes.Directory(i);
            string name = SweepShapes.FileName(i);
            PooledPhysicalFile file = new();
            file.SetLocation(directory, name);
            file.Root = SweepShapes.Root;
            file.LastWritten = DateTimeOffset.UnixEpoch;
            PooledFileOperation op = new();
            op.SetLocation(directory, name);
            op.Root = SweepShapes.Root;
            op.Kind = OperationKind.Deleted;
            op.SubjectIndex = i;
            _files.Add(file);
            _ops.Add(op);
            _names[i] = name;
        }

        // The shape numbers, printed once per parameter combination. They are a property of the
        // chunking rule, not of the run, so measuring them per iteration would only add noise.
        FrameStats stats = Frame();
        Console.WriteLine(
            $"frames: budget {ChunkByteBudget / 1024} KiB, {EntryCount:N0} entries -> {stats.Frames:N0} frames, " +
            $"largest {stats.LargestBytes:N0} B, {stats.LargeObjectHeapFrames:N0} at or above the {LargeObjectHeapThreshold:N0} B LOH threshold");
    }

    /// <summary>Converts and serializes the whole sweep at the configured budget — the per-frame work
    /// the service does on the streamed path, minus the pipe.</summary>
    [Benchmark]
    public long SerializeChunks() => Frame().LargestBytes;

    private FrameStats Frame()
    {
        DryRunStreamHandler.WireChunkConverter converter = new();
        List<IPhysicalFileView> files = [];
        List<IFileOperationView> ops = [];
        long budgetUsed = 0;
        FrameStats stats = default;

        for (int i = 0; i < _files.Count; i++)
        {
            files.Add(_files[i]);
            ops.Add(_ops[i]);
            budgetUsed += DryRunEngine.WireFileUpperBoundBytes(_names[i])
                + DryRunEngine.WireOpUpperBoundBytes(_names[i], null);
            if (budgetUsed < ChunkByteBudget)
                continue;

            stats = Emit(converter, files, ops, stats);
            files = [];
            ops = [];
            budgetUsed = 0;
        }
        if (files.Count > 0)
            stats = Emit(converter, files, ops, stats);

        return stats;
    }

    private static FrameStats Emit(
        DryRunStreamHandler.WireChunkConverter converter,
        List<IPhysicalFileView> files, List<IFileOperationView> ops, FrameStats stats)
    {
        // SerializeResponse returns a fresh exact-size byte[] — the array whose size decides whether
        // this frame lands on the LOH.
        byte[] payload = IpcSerializer.SerializeResponse(converter.Convert(new DryRunChunk([], files, [], ops)));
        return new FrameStats(
            stats.Frames + 1,
            Math.Max(stats.LargestBytes, payload.Length),
            stats.LargeObjectHeapFrames + (payload.Length >= LargeObjectHeapThreshold ? 1 : 0));
    }

    private readonly record struct FrameStats(int Frames, long LargestBytes, int LargeObjectHeapFrames);
}
