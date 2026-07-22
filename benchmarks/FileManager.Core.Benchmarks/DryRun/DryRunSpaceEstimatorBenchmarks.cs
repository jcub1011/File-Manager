using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.DryRun;
using FileManager.Core.Platform;

namespace FileManager.Core.Benchmarks.DryRun;

/// <summary>Measures <see cref="DryRunSpaceEstimator"/> in isolation over pre-built synthetic chunks
/// shaped like the streaming handler's real feed (~2k files per chunk, two destination roots on one
/// volume, 10% overwrites, 10% permanent-delete sources). This is the entire cost the space
/// projection adds to a dry run — everything it consumes is data the run already produced — so this
/// number is the ceiling on what the storage calculation can slow down. The volume provider mimics
/// the real key normalization (GetFullPath + GetPathRoot + lower) so the per-root memoization is
/// exercised realistically, with no P/Invoke.</summary>
[MemoryDiagnoser]
public class DryRunSpaceEstimatorBenchmarks
{
    private const int ChunkSize = 2_000;

    /// <summary>Total destination operations folded per invocation. 100k ≈ a large real run; 500k is
    /// the engine's streaming candidate cap (<see cref="DryRunEngine.MaxStreamedFiles"/>).</summary>
    [Params(100_000, 500_000)]
    public int OperationCount { get; set; }

    private List<Chunk> _chunks = null!;
    private FakeVolumes _volumes = null!;

    private sealed record Chunk(
        PhysicalFile[] SourceFiles,
        PhysicalFile[] DestinationFiles,
        VirtualFileOperation[] SourceOps,
        VirtualFileOperation[] DestinationOps,
        int SourceBase,
        int DestBase);

    [GlobalSetup]
    public void Setup()
    {
        _volumes = new FakeVolumes();
        _chunks = [];
        int sourceBase = 0;
        int destBase = 0;

        while (sourceBase < OperationCount)
        {
            int count = Math.Min(ChunkSize, OperationCount - sourceBase);
            var sourceFiles = new PhysicalFile[count];
            var sourceOps = new VirtualFileOperation[count];
            var destinationFiles = new List<PhysicalFile>();
            var destinationOps = new VirtualFileOperation[count];

            for (int j = 0; j < count; j++)
            {
                int i = sourceBase + j;
                string root = (i & 1) == 0 ? @"D:\dest\one" : @"D:\dest\two";
                long length = 1_000 + (i % 64) * 512;

                sourceFiles[j] = new PhysicalFile
                {
                    Path = $@"C:\src\dir-{i % 100}\file-{i}.dat",
                    Root = @"C:\src",
                    Length = length,
                    LastWritten = DateTimeOffset.UnixEpoch,
                };
                sourceOps[j] = new VirtualFileOperation
                {
                    Path = sourceFiles[j].Path,
                    Root = @"C:\src",
                    Kind = OperationKind.Processed,
                    SourceIndex = i,
                    SubjectIndex = -1,
                    SourceDisposition = i % 10 == 3 ? OnSuccessAction.PermanentDelete : OnSuccessAction.KeepSource,
                };

                // Every tenth file overwrites a pre-existing destination file; the rest are new writes.
                if (i % 10 == 0)
                {
                    destinationOps[j] = new VirtualFileOperation
                    {
                        Path = $@"{root}\file-{i}.dat",
                        Root = root,
                        Kind = OperationKind.Overwrite,
                        SourceIndex = i,
                        SubjectIndex = destBase + destinationFiles.Count,
                    };
                    destinationFiles.Add(new PhysicalFile
                    {
                        Path = $@"{root}\file-{i}.dat",
                        Root = root,
                        Length = length / 2,
                        LastWritten = DateTimeOffset.UnixEpoch,
                    });
                }
                else
                {
                    destinationOps[j] = new VirtualFileOperation
                    {
                        Path = $@"{root}\file-{i}.dat",
                        Root = root,
                        Kind = OperationKind.New,
                        SourceIndex = i,
                        SubjectIndex = -1,
                    };
                }
            }

            _chunks.Add(new Chunk(sourceFiles, [.. destinationFiles], sourceOps, destinationOps, sourceBase, destBase));
            sourceBase += count;
            destBase += destinationFiles.Count;
        }
    }

    [Benchmark]
    public SpaceProjection AccumulateAndFinalize()
    {
        var estimator = new DryRunSpaceEstimator(_volumes, safetyMarginBytes: 512L * 1024 * 1024);
        foreach (Chunk c in _chunks)
            estimator.Accumulate(
                c.SourceFiles, c.DestinationFiles, c.SourceOps, c.DestinationOps,
                c.SourceBase, c.DestBase, stageOverwrites: false);
        return estimator.Finalize(concurrency: 4);
    }

    /// <summary>Same key shape as <c>WindowsVolumeInfoProvider</c> (full-path normalization, lowered
    /// drive root) with a fixed in-memory capacity — realistic per-call cost, no P/Invoke.</summary>
    private sealed class FakeVolumes : IVolumeInfoProvider
    {
        public Result<long, string> GetAvailableFreeBytes(string path) => long.MaxValue / 2;
        public bool IsNetworkPath(string path) => false;
        public DriveClass GetDriveClass(string path) => DriveClass.Fixed;

        public Result<string, string> GetVolumeKey(string path)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return Result<string, string>.Success(Path.TrimEndingDirectorySeparator(root).ToLowerInvariant());
        }

        public Result<VolumeCapacity, string> GetVolumeCapacity(string path) =>
            new VolumeCapacity(4_000_000_000_000, 1_500_000_000_000, 4096);
    }
}
