using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Core.DryRun;
using FileManager.Core.IPC.Handlers;
using Xunit.Abstractions;

namespace FileManager.Core.Tests.DryRun;

/// <summary>The per-entry allocation gauge for the wire conversion path — the CI regression gate for
/// the dry-run allocation-avoidance work.
///
/// In-run peak memory tracks allocation CHURN, not the live set: the streamed pipeline keeps almost
/// nothing alive, but every byte allocated per entry is a byte the GC must absorb during the burst,
/// and at 500k swept entries the converter's strings alone were ~40% of the run's total churn. This
/// pins the converter's steady-state bytes/entry so a later change cannot quietly reintroduce
/// per-entry garbage (the way <c>Path.GetDirectoryName</c> once allocated a throwaway string per
/// file/op purely to probe the directory-table dictionary).
///
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> is exact for the current thread and the
/// converter is synchronous, so the measurement is deterministic apart from JIT one-time costs, which
/// the warm-up pass absorbs. Numbers here are JIT-host numbers, not the AOT service's — directionally
/// exact, which is what a regression gate needs (the published binary's totals are logged per run by
/// <c>DryRunStreamHandler</c> as <c>allocated {N}MB</c>).</summary>
public sealed class WireChunkConverterAllocationTests(ITestOutputHelper output)
{
    /// <summary>Steady-state ceiling per (file, op) pair, with the pair sharing one path string —
    /// the destination-sweep shape, which is the volume case. Measured at ~90-char paths: 627 B/pair
    /// originally (two throwaway <c>GetDirectoryName</c> strings, two <c>GetFileName</c> strings,
    /// fresh wire records); 254 after the span-probe/memo pass removed the throwaway strings; 113
    /// after wire-DTO recycling removed the per-entry records and chunk lists — what remains is the
    /// one file-name string the wire genuinely carries, plus amortized odds and ends. The headroom
    /// above 113 is for runtime/layout variation across machines, not for new allocations.</summary>
    private const int MaxBytesPerEntryPair = 200;

    private const int EntriesPerChunk = 300;   // ~ one WireChunkByteBudget chunk's worth
    private const int Chunks = 60;
    private const int FilesPerDirectory = 200;

    [Fact]
    public void Converter_steady_state_allocation_per_entry_stays_under_ceiling()
    {
        // Build every input up front: nothing but the converter may allocate inside the bracket.
        List<DryRunChunk> chunks = BuildSweepShapedChunks(Chunks, EntriesPerChunk);
        DryRunChunk warmupChunk = BuildSweepShapedChunks(1, EntriesPerChunk)[0];

        // Warm-up with a throwaway converter: absorbs JIT/static one-time allocations without
        // pre-seeding the measured converter's directory table (table growth is a real production
        // cost and belongs inside the bracket).
        DryRunStreamHandler.WireChunkConverter warmup = new();
        warmup.Convert(warmupChunk);

        DryRunStreamHandler.WireChunkConverter converter = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (DryRunChunk chunk in chunks)
            converter.Convert(chunk);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        double perPair = (double)allocated / (Chunks * EntriesPerChunk);
        output.WriteLine($"wire conversion: {perPair:F0} B per (file, op) pair (ceiling {MaxBytesPerEntryPair} B)");
        Assert.True(
            perPair <= MaxBytesPerEntryPair,
            $"wire conversion allocated {perPair:F0} B per (file, op) pair (ceiling {MaxBytesPerEntryPair} B). " +
            "Something on the per-entry conversion path has started allocating; find it rather than raising the ceiling.");
    }

    /// <summary>The memo is an allocation shortcut, never a semantic one: an op whose Path/Root are
    /// the same string INSTANCES as its file's (the sweep shape) must convert to exactly what it
    /// would have without the sharing. Distinct-but-equal strings take the non-memo path; both must
    /// land on the same wire values.</summary>
    [Fact]
    public void Shared_reference_ops_convert_identically_to_distinct_equal_strings()
    {
        const string root = @"C:\fm-gauge\destination-tree";
        const string path = root + @"\sub\file.dat";
        PhysicalFile file = new() { Path = path, Root = root, Length = 7, LastWritten = DateTimeOffset.UnixEpoch };
        VirtualFileOperation sharedOp = new()
        {
            Path = path,                                         // same instances → memo hit
            Root = root,
            Kind = OperationKind.Untouched,
            SubjectIndex = 0,
        };
        VirtualFileOperation distinctOp = sharedOp with
        {
            Path = new string(path.AsSpan()),                    // equal, different instances → memo miss
            Root = new string(root.AsSpan()),
        };

        DryRunStreamHandler.WireChunkConverter viaMemo = new();
        DryRunChunkResponse hit = viaMemo.Convert(new DryRunChunk([], [file], [], [sharedOp]));
        DryRunStreamHandler.WireChunkConverter viaFull = new();
        DryRunChunkResponse miss = viaFull.Convert(new DryRunChunk([], [file], [], [distinctOp]));

        Assert.Equal(hit.Directories, miss.Directories);
        Assert.Equal(hit.DestinationFiles, miss.DestinationFiles);
        Assert.Equal(hit.DestinationOperations, miss.DestinationOperations);
    }

    /// <summary>Chunks in the destination sweep's shape: destination files + destination ops only,
    /// each op's <c>Path</c>/<c>Root</c> REFERENCE-EQUAL to its file's (as
    /// <c>DestinationProjector</c> produces them), ~90-char paths, <see cref="FilesPerDirectory"/>
    /// files per directory so the directory table hits far more often than it misses.</summary>
    private static List<DryRunChunk> BuildSweepShapedChunks(int chunkCount, int entriesPerChunk)
    {
        const string root = @"C:\fm-gauge\destination-tree";
        List<DryRunChunk> chunks = new(chunkCount);
        int fileIndex = 0;
        for (int c = 0; c < chunkCount; c++)
        {
            List<PhysicalFile> files = new(entriesPerChunk);
            List<VirtualFileOperation> ops = new(entriesPerChunk);
            for (int i = 0; i < entriesPerChunk; i++, fileIndex++)
            {
                string path = $@"{root}\level-one-padding\dir{fileIndex / FilesPerDirectory:D6}\file-{fileIndex:D8}-with-a-realistic-name.dat";
                files.Add(new PhysicalFile
                {
                    Path = path,
                    Root = root,
                    Length = 0,
                    LastWritten = DateTimeOffset.UnixEpoch,
                });
                ops.Add(new VirtualFileOperation
                {
                    Path = path,
                    Root = root,
                    Kind = OperationKind.Untouched,
                    SubjectIndex = i,
                });
            }
            chunks.Add(new DryRunChunk([], files, [], ops));
        }
        return chunks;
    }
}
