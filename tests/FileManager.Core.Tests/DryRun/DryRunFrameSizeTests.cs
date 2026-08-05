using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Scanning;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace FileManager.Core.Tests.DryRun;

/// <summary>Guards the size of every frame the streamed dry run puts on the wire.
///
/// A <c>byte[]</c> of 85,000 bytes or more is allocated on the Large Object Heap, which is not
/// compacted by default and is only collected with a gen2. The service serializes each response into
/// a fresh exact-size array (<c>IpcSerializer.SerializeResponse</c>), so an oversized frame is not
/// merely a big write — it is a permanent addition to the process's committed footprint that survives
/// the run. At the reported workload (357k swept destination files) the sweep alone produced roughly
/// 88 such frames.
///
/// This asserts a property of the CHUNKING CONSTANTS, not of the serializer: it is what stops someone
/// raising a chunk size back to "a few thousand entries" because the 16 MiB protocol cap has plenty of
/// room. The protocol cap is not the binding constraint; the LOH threshold is.
///
/// Scope: the frames the HANDLER emits — the destination-sweep chunks, whose size the handler alone
/// controls. Both phases now budget against the same
/// <see cref="DryRunEngine.WireChunkByteBudget"/>, but the engine's file-phase chunking is not
/// exercised here: a stub engine's chunks are whatever the stub chose. Guarding that half needs a test
/// against the real engine over a real tree.</summary>
public sealed class DryRunFrameSizeTests
{
    /// <summary>The Large Object Heap threshold. Frames at or above this are LOH allocations.</summary>
    private const int LargeObjectHeapThreshold = 85_000;

    /// <summary>Long enough that a realistic path (~90 chars) dominates each wire record, which is the
    /// shape that actually blows the frame budget. A short-name tree would pass a frame-size test while
    /// the real workload failed it.</summary>
    private const int SyntheticNameLength = 64;

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeCatalog(params Profile[] profiles) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = profiles;
        public IReadOnlyList<Profile> Active => All.Where(p => p.Active).ToList();
        public IDisposable Subscribe(Action changeHandler) => throw new NotSupportedException();
        public Result Reload() => Result.Success();
    }

    /// <summary>Emits <paramref name="totalFiles"/> source files in one chunk, with long paths. The
    /// handler forwards this verbatim, so it only needs to be big enough to give the sweep a non-zero
    /// <c>destinationCount</c> offset to work against.</summary>
    private sealed class FakeStreamEngine(int totalFiles) : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Profile profile, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
            Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            const string root = @"C:\fm-test\source";
            List<PhysicalFile> files = [];
            List<VirtualFileOperation> ops = [];
            for (int i = 0; i < totalFiles; i++)
            {
                string path = SyntheticPath(root, i);
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
                    Kind = OperationKind.Processed,
                    SourceIndex = i,
                });
            }
            yield return Result<DryRunChunk, string>.Success(new DryRunChunk(files, [], ops, []));
            await Task.Yield();
        }
    }

    /// <summary>A scheduler whose session yields <paramref name="filesPerRoot"/> synthetic files under
    /// each submitted root without touching disk — which is what makes a sweep of tens of thousands of
    /// entries affordable in a unit test. It honours the session's <c>OnFile</c> policy so survivor
    /// filtering, temp-file exclusion and the sweep budget all still run, exactly as they would against
    /// the real scheduler (which also applies <c>OnFile</c> before emitting).</summary>
    private sealed class SyntheticScanScheduler(int filesPerRoot, int filesPerDirectory = 200) : IScanScheduler
    {
        public IScanSession OpenSession(ScanSessionOptions options, CancellationToken ct) =>
            new Session(options, filesPerRoot, filesPerDirectory, ct);

        private sealed class Session(
            ScanSessionOptions options, int filesPerRoot, int filesPerDirectory, CancellationToken ct) : IScanSession
        {
            private readonly List<ScanWorkItem> _roots = [];

            public void Submit(ScanWorkItem item) => _roots.Add(item);

            public void CompleteSubmissions() { }

            public IEnumerable<ScanResult> Consume()
            {
                foreach (ScanWorkItem root in _roots)
                {
                    for (int i = 0; i < filesPerRoot; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        FileSystemEntry entry = new(
                            fileName: SyntheticName(i),
                            fullPath: SyntheticPath(root.Directory, i, filesPerDirectory),
                            isDirectory: false,
                            size: 0,
                            modified: DateTimeOffset.UnixEpoch);
                        if (!options.OnFile(entry, root.Tag))
                            continue;
                        yield return new ScanResult(entry, null, root.Tag);
                    }
                }
            }

            public void Dispose() { }
        }
    }

    // ── Synthetic path shape ─────────────────────────────────────────────────────────────────────

    private static string SyntheticName(int index) =>
        index.ToString("D8") + new string('n', SyntheticNameLength - 12) + ".dat";

    /// <summary>A ~90-character path: root + one subdirectory level + a long file name. The
    /// subdirectory level matters — the wire contract normalizes directories into a shared table, so a
    /// flat tree would understate per-record cost relative to the real workload, and a
    /// directory-per-file tree (<paramref name="filesPerDirectory"/> of 1) maximizes it: every entry
    /// then also drags a fresh <c>DryRunDirectory</c> record into its frame.</summary>
    private static string SyntheticPath(string root, int index, int filesPerDirectory = 200) =>
        Path.Combine(root, "sub" + (index / filesPerDirectory).ToString("D6"), SyntheticName(index));

    private static string SyntheticPath(string root, int index) => SyntheticPath(root, index, 200);

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────

    private static DryRunStreamHandler NewHandler(
        Profile profile, IDryRunEngine engine, int sweptFiles, int filesPerDirectory = 200) =>
        new(NullLogger<DryRunStreamHandler>.Instance,
            new ProfilePlanner(
                NullLogger<ProfilePlanner>.Instance, engine,
                new DestinationProjector(
                    NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(),
                    new SyntheticScanScheduler(sweptFiles, filesPerDirectory)),
                new FakeVolumeInfoProvider(), new EngineConfig()),
            new FakeCatalog(profile), TimeProvider.System,
            new EngineEventBus(NullLogger<EngineEventBus>.Instance), NullMemoryTrimCoordinator.Instance)
        // Sizes are serialized inline, but CollectWithSizes also buffers the frame OBJECTS for the
        // post-hoc swept-count check — recycling would corrupt those (the seam's documented case).
        { MaxStreamedFiles = 1_000_000, RecycleWireRecords = false };

    private static async Task<List<(IpcResponse Frame, int Bytes)>> CollectWithSizes(
        DryRunStreamHandler handler, Guid profileId)
    {
        List<(IpcResponse, int)> frames = [];
        await foreach (IpcResponse frame in handler.HandleStreamAsync(
            new DryRunStreamRequest { ProfileId = profileId }))
        {
            frames.Add((frame, IpcSerializer.SerializeResponse(frame).Length));
        }
        return frames;
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    // The realistic shape (the reported workload was ~200 files per leaf directory) …
    [InlineData(200)]
    // … and the worst case for frame size: every entry also carries a brand-new directory-table
    // record, which the chunk estimate does not itself account for. This is the case that proves the
    // budget's headroom is real rather than an artifact of directory sharing.
    [InlineData(1)]
    public async Task Every_streamed_frame_stays_off_the_large_object_heap(int filesPerDirectory)
    {
        // Mirror so the sweep classifies every synthetic file as a Deleted orphan and emits it — the
        // maximum-output case, and the one the reported workload actually hit.
        Profile profile = TestProfiles.Valid() with { SyncMode = SyncMode.Mirror, ScanDestination = true };
        DryRunStreamHandler handler = NewHandler(
            profile, new FakeStreamEngine(totalFiles: 8), sweptFiles: 20_000, filesPerDirectory);

        List<(IpcResponse Frame, int Bytes)> frames = await CollectWithSizes(handler, profile.Id);

        // Sanity: the sweep really did emit, so a silently-empty sweep can't make this test vacuous.
        int swept = frames.Select(f => f.Frame).OfType<DryRunChunkResponse>()
            .SelectMany(c => c.DestinationOperations.Kind)
            .Count(k => k == OperationKind.Deleted);
        Assert.Equal(20_000, swept);

        (IpcResponse Frame, int Bytes)[] oversized =
            [.. frames.Where(f => f.Bytes >= LargeObjectHeapThreshold)];
        Assert.True(
            oversized.Length == 0,
            oversized.Length == 0 ? "" :
                $"{oversized.Length} of {frames.Count} frames landed on the Large Object Heap " +
                $"(>= {LargeObjectHeapThreshold:N0} bytes). Largest: {oversized.Max(f => f.Bytes):N0} bytes, " +
                $"a {oversized[0].Frame.GetType().Name}. Lower the chunk budget that produced them.");
    }

    [Fact]
    public async Task Frame_count_stays_proportionate_when_the_sweep_is_chunked_smaller()
    {
        // The counterweight to the test above: shrinking frames must not explode the frame count into
        // per-entry frames, whose named-pipe write overhead would dominate the run.
        Profile profile = TestProfiles.Valid() with { SyncMode = SyncMode.Mirror, ScanDestination = true };
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 8), sweptFiles: 20_000);

        List<(IpcResponse Frame, int Bytes)> frames = await CollectWithSizes(handler, profile.Id);

        int chunkFrames = frames.Count(f => f.Frame is DryRunChunkResponse);
        // 20,000 entries: at least a handful of frames (we are deliberately below the LOH line), but
        // far fewer than one per ~25 entries.
        Assert.InRange(chunkFrames, 2, 800);
    }
}
