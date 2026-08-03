using FileManager.Contracts.DryRun;
using FileManager.Core.DryRun;

namespace FileManager.Core.Benchmarks.Fixtures;

/// <summary>The in-memory destination-sweep shape the allocation benchmarks measure against, in one
/// place because three classes need exactly the same one and a divergence between them would make
/// their numbers incomparable.
///
/// <para><b>Path length is load-bearing, not cosmetic.</b> Every per-entry cost these benchmarks
/// weigh — a directory string, a joined path, a file name on the wire — scales with the path. The
/// ~96-character two-level shape below is the one <c>WireChunkConverterAllocationTests</c> measured
/// the 627 / 254 / 113 B-per-pair figures at, and the one
/// <c>tools/FileManager.MemoryProbe/TreeBuilder.cs</c> calls the "realistic deep paths" case. Short
/// names would make every optimization here look better than it is on real data.</para>
///
/// <para><b>String identity is load-bearing too.</b> The sweep hands a file and its operation the
/// SAME path instance, and every sibling in a directory the SAME directory instance
/// (<c>FileSystemEntry.InDirectory</c>). Both the converter's reference memo and the directory
/// table's memo key on reference equality, so a fixture that handed out equal-but-distinct strings
/// would silently measure the miss path and understate the optimization.</para></summary>
internal static class SweepShapes
{
    /// <summary>Matches the sweep's per-directory fan-out in the reported workloads (and the
    /// MemoryProbe's <c>--files-per-dir</c> default), which is what makes the directory table hit far
    /// more often than it misses — the ratio the Stage 1 claim is stated in.</summary>
    internal const int FilesPerDirectory = 200;

    internal const string Root = @"C:\fm-bench\destination-tree";

    /// <summary>The containing directory for an entry. Callers that want the production shape must
    /// cache this per <see cref="FilesPerDirectory"/> group and share the instance — see the class
    /// remarks.</summary>
    internal static string Directory(int index) =>
        $@"{Root}\level-one-padding\dir{index / FilesPerDirectory:D6}";

    internal static string FileName(int index) => $"file-{index:D8}-with-a-realistic-name.dat";

    /// <summary>The joined absolute path — ~96 characters, the shape the claims were measured at.</summary>
    internal static string SyntheticPath(int index) => Directory(index) + '\\' + FileName(index);

    /// <summary>Chunks in the shape a producer that materializes paths emits: immutable
    /// <see cref="PhysicalFile"/>/<see cref="VirtualFileOperation"/> records, each pair sharing one
    /// path string instance. This is what the sweep looked like before Stage 4 taught it to carry
    /// (directory, name) instead.</summary>
    internal static List<DryRunChunk> StringPathChunks(int chunkCount, int entriesPerChunk)
    {
        List<DryRunChunk> chunks = new(chunkCount);
        int index = 0;
        for (int c = 0; c < chunkCount; c++)
        {
            List<IPhysicalFileView> files = new(entriesPerChunk);
            List<IFileOperationView> ops = new(entriesPerChunk);
            for (int i = 0; i < entriesPerChunk; i++, index++)
            {
                string path = SyntheticPath(index);   // one instance, shared by the pair
                files.Add(new PhysicalFile
                {
                    Path = path,
                    Root = Root,
                    Length = 0,
                    LastWritten = DateTimeOffset.UnixEpoch,
                });
                ops.Add(new VirtualFileOperation
                {
                    Path = path,
                    Root = Root,
                    Kind = OperationKind.Deleted,
                    SubjectIndex = index,
                });
            }
            chunks.Add(new DryRunChunk([], files, [], ops));
        }
        return chunks;
    }

    /// <summary>Chunks in the shape the streamed sweep actually emits today: pooled carriers holding
    /// the (directory, name) PAIR, with one directory instance per <see cref="FilesPerDirectory"/>
    /// siblings and the file-name instance shared by the pair. <c>DirectoryHint</c> is what selects
    /// the wire converter's allocation-free fast path, so it must be populated — reading
    /// <c>Path</c> on one of these would join (and cache) a string the production path never
    /// materializes.</summary>
    internal static List<DryRunChunk> PooledCarrierChunks(int chunkCount, int entriesPerChunk)
    {
        List<DryRunChunk> chunks = new(chunkCount);
        int index = 0;
        string directory = Directory(0);
        int directoryGroup = 0;
        for (int c = 0; c < chunkCount; c++)
        {
            List<IPhysicalFileView> files = new(entriesPerChunk);
            List<IFileOperationView> ops = new(entriesPerChunk);
            for (int i = 0; i < entriesPerChunk; i++, index++)
            {
                if (index / FilesPerDirectory != directoryGroup)
                {
                    directoryGroup = index / FilesPerDirectory;
                    directory = Directory(index);
                }
                string name = FileName(index);   // one instance, shared by the pair
                PooledPhysicalFile file = new();
                file.SetLocation(directory, name);
                file.Root = Root;
                file.Length = 0;
                file.LastWritten = DateTimeOffset.UnixEpoch;
                PooledFileOperation op = new();
                op.SetLocation(directory, name);
                op.Root = Root;
                op.Kind = OperationKind.Deleted;
                op.SubjectIndex = index;
                files.Add(file);
                ops.Add(op);
            }
            chunks.Add(new DryRunChunk([], files, [], ops));
        }
        return chunks;
    }
}
