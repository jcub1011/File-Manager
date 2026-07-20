using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace FileManager.Core.Tests.IPC.Handlers;

public sealed class DryRunStreamHandlerTests
{
    private sealed class FakeCatalog(params Profile[] profiles) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = profiles;
        public IReadOnlyList<Profile> Active => All.Where(p => p.Active).ToList();
        public IDisposable Subscribe(Action changeHandler) => throw new NotSupportedException();
        public Result Reload() => Result.Success();
    }

    /// <summary>Yields <paramref name="totalFiles"/> WouldProcess results in fixed-size chunks; the
    /// handler decides where (if anywhere) to truncate.</summary>
    private sealed class FakeStreamEngine(int totalFiles, int chunkSize) : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Profile profile, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
            Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int emitted = 0; emitted < totalFiles;)
            {
                int n = Math.Min(chunkSize, totalFiles - emitted);
                int start = emitted;
                List<PhysicalFile> files = Enumerable.Range(0, n)
                    .Select(i => new PhysicalFile
                    {
                        Path = $@"C:\x\{start + i}.dat",
                        Root = @"C:\x",
                        Length = 0,
                        LastWritten = DateTimeOffset.UnixEpoch,
                    })
                    .ToList();
                List<VirtualFileOperation> ops = Enumerable.Range(0, n)
                    .Select(i => new VirtualFileOperation
                    {
                        Path = $@"C:\x\{start + i}.dat",
                        Root = @"C:\x",
                        Kind = OperationKind.Processed,
                        SourceIndex = start + i,
                    })
                    .ToList();
                emitted += n;
                yield return Result<DryRunChunk, string>.Success(new DryRunChunk(files, [], ops, []));
                await Task.Yield();
            }
        }
    }

    private static DryRunStreamHandler NewHandler(Profile profile, IDryRunEngine engine, int maxStreamedFiles)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        return new(NullLogger<DryRunStreamHandler>.Instance, engine, new FakeCatalog(profile), TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, fileSystem, new FakeVolumeInfoProvider()),
            new FakeVolumeInfoProvider(), new EngineConfig(), new FakeSettingsProvider())
        { MaxStreamedFiles = maxStreamedFiles };
    }

    private static async Task<List<IpcResponse>> Collect(DryRunStreamHandler handler, Guid profileId) =>
        await Collect(handler, new DryRunStreamRequest { ProfileId = profileId });

    private static async Task<List<IpcResponse>> Collect(DryRunStreamHandler handler, DryRunStreamRequest request)
    {
        List<IpcResponse> frames = [];
        await foreach (IpcResponse frame in handler.HandleStreamAsync(request))
            frames.Add(frame);
        return frames;
    }

    /// <summary>Builds a handler whose catalog is empty, so only an inline profile can drive a run.</summary>
    private static DryRunStreamHandler NewHandlerEmptyCatalog(IDryRunEngine engine, int maxStreamedFiles)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        return new(NullLogger<DryRunStreamHandler>.Instance, engine, new FakeCatalog(), TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, fileSystem, new FakeVolumeInfoProvider()),
            new FakeVolumeInfoProvider(), new EngineConfig(), new FakeSettingsProvider())
        { MaxStreamedFiles = maxStreamedFiles };
    }

    [Fact]
    public async Task Truncates_and_marks_the_completion_when_the_emitted_cap_is_reached()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 100, chunkSize: 4), maxStreamedFiles: 8);

        List<IpcResponse> frames = await Collect(handler, profile.Id);

        DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.True(complete.Truncated);
        int emitted = frames.OfType<DryRunChunkResponse>().Sum(c => c.SourceFiles.Count);
        Assert.Equal(8, emitted);   // stops at the cap rather than forwarding all 100
    }

    [Fact]
    public async Task Does_not_mark_the_completion_when_the_report_fits_under_the_cap()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 8, chunkSize: 4), maxStreamedFiles: 500);

        List<IpcResponse> frames = await Collect(handler, profile.Id);

        DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.False(complete.Truncated);
        Assert.Equal(8, frames.OfType<DryRunChunkResponse>().Sum(c => c.SourceFiles.Count));
    }

    [Fact]
    public async Task Unknown_profile_is_a_single_PROFILE_NOT_FOUND_frame()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 4, chunkSize: 4), maxStreamedFiles: 500);

        List<IpcResponse> frames = await Collect(handler, Guid.NewGuid());

        ErrorResponse error = Assert.IsType<ErrorResponse>(Assert.Single(frames));
        Assert.Equal("PROFILE_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task Inline_profile_previews_a_draft_absent_from_the_catalog()
    {
        Profile draft = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandlerEmptyCatalog(
            new FakeStreamEngine(totalFiles: 4, chunkSize: 4), maxStreamedFiles: 500);

        // Empty catalog → the id alone is unknown; the inline draft drives the run instead.
        List<IpcResponse> frames = await Collect(handler,
            new DryRunStreamRequest { ProfileId = draft.Id, InlineProfile = draft });

        Assert.DoesNotContain(frames, f => f is ErrorResponse);
        Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.Equal(4, frames.OfType<DryRunChunkResponse>().Sum(c => c.SourceFiles.Count));
    }

    // ── Destination sweep (orphans / scan-truncation / sweep cap) ───────────────────────────────
    // These drive the same HandleStreamAsync but with a scripted engine (so we control ScanTruncated)
    // against a real target root on disk, exercising the handler's post-file-phase destination sweep.

    /// <summary>Yields caller-supplied chunks verbatim so a test can inject ScanTruncated and control
    /// the streamed source/destination slices.</summary>
    private sealed class ScriptedStreamEngine(params DryRunChunk[] chunks) : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Profile profile, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
            Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (DryRunChunk chunk in chunks)
            {
                yield return Result<DryRunChunk, string>.Success(chunk);
                await Task.Yield();
            }
        }
    }

    private static PhysicalFile Phys(string path, string root) =>
        new() { Path = path, Root = root, Length = 0, LastWritten = DateTimeOffset.UnixEpoch };

    private static VirtualFileOperation Op(string path, string root, OperationKind kind, int sourceIndex) =>
        new() { Path = path, Root = root, Kind = kind, SourceIndex = sourceIndex };

    private static List<OperationKind> DeletedKinds(List<IpcResponse> frames) =>
        frames.OfType<DryRunChunkResponse>()
            .SelectMany(f => f.DestinationOperations)
            .Where(o => o.Kind == OperationKind.Deleted)
            .Select(o => o.Kind)
            .ToList();

    [Fact]
    public async Task Mirror_sweep_emits_deleted_orphans_with_globally_offset_subject_indices()
    {
        string root = Path.Combine(Path.GetTempPath(), "fm-drs-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            // Real orphan files under the target root that no streamed source writes to.
            File.WriteAllText(Path.Combine(target, "orphan1.txt"), "x");
            File.WriteAllText(Path.Combine(target, "orphan2.txt"), "x");

            Profile profile = TestProfiles.Valid(source, target) with { SyncMode = SyncMode.Mirror };

            // One streamed chunk carrying a source write plus one destination file, so
            // destinationCount == 1 and the sweep's SubjectIndex values are offset by it.
            string written = Path.Combine(target, "written.txt");   // a survivor (not on disk, never swept)
            DryRunChunk chunk = new(
                SourceFiles: [Phys(@"C:\src\a.dat", @"C:\src")],
                DestinationFiles: [Phys(written, target)],
                SourceOperations: [Op(@"C:\src\a.dat", @"C:\src", OperationKind.Processed, 0)],
                DestinationOperations: [Op(written, target, OperationKind.New, 0)]);

            DryRunStreamHandler handler = NewHandler(profile, new ScriptedStreamEngine(chunk), maxStreamedFiles: 500);
            List<IpcResponse> frames = await Collect(handler, profile.Id);

            List<DryRunOperation> deleted = frames.OfType<DryRunChunkResponse>()
                .SelectMany(f => f.DestinationOperations)
                .Where(o => o.Kind == OperationKind.Deleted)
                .ToList();

            Assert.Equal(2, deleted.Count);
            Assert.All(deleted, o => Assert.Contains("orphan", o.FileName));
            // destinationCount (1) + position → global SubjectIndex values are {1, 2}.
            Assert.Equal([1, 2], deleted.Select(o => o.SubjectIndex).OrderBy(i => i).ToArray());

            DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
            Assert.False(complete.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Additive_sweep_is_gated_on_the_profile_scan_flag()
    {
        string root = Path.Combine(Path.GetTempPath(), "fm-drs-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            // A pre-existing target-only file: swept as Untouched in Additive only when scanning is on.
            File.WriteAllText(Path.Combine(target, "preexisting.txt"), "x");

            string written = Path.Combine(target, "written.txt");   // a survivor
            DryRunChunk Chunk() => new(
                SourceFiles: [Phys(@"C:\src\a.dat", @"C:\src")],
                DestinationFiles: [Phys(written, target)],
                SourceOperations: [Op(@"C:\src\a.dat", @"C:\src", OperationKind.Processed, 0)],
                DestinationOperations: [Op(written, target, OperationKind.New, 0)]);

            // Scan off (the default): no destination-only Untouched row despite the file on disk.
            Profile off = TestProfiles.Valid(source, target);   // AdditiveArchive, ScanDestination = false
            List<IpcResponse> offFrames = await Collect(
                NewHandler(off, new ScriptedStreamEngine(Chunk()), maxStreamedFiles: 500), off.Id);
            Assert.DoesNotContain(
                offFrames.OfType<DryRunChunkResponse>().SelectMany(f => f.DestinationOperations),
                o => o.Kind == OperationKind.Untouched);

            // Scan on: the sweep runs and surfaces the pre-existing file as Untouched.
            Profile on = off with { ScanDestination = true };
            List<IpcResponse> onFrames = await Collect(
                NewHandler(on, new ScriptedStreamEngine(Chunk()), maxStreamedFiles: 500), on.Id);
            Assert.Contains(
                onFrames.OfType<DryRunChunkResponse>().SelectMany(f => f.DestinationOperations),
                o => o.Kind == OperationKind.Untouched && o.FileName.Contains("preexisting"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Scan_truncation_suppresses_the_mirror_sweep_and_marks_the_report_truncated()
    {
        string root = Path.Combine(Path.GetTempPath(), "fm-drs-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            // Orphans exist on disk, but the scan was cut short — a prefix-only survivor set must NOT
            // drive a (bogus) Mirror-deletion preview.
            File.WriteAllText(Path.Combine(target, "orphan1.txt"), "x");
            File.WriteAllText(Path.Combine(target, "orphan2.txt"), "x");

            Profile profile = TestProfiles.Valid(source, target) with { SyncMode = SyncMode.Mirror };

            DryRunChunk truncatedChunk = new(
                SourceFiles: [Phys(@"C:\src\a.dat", @"C:\src")],
                DestinationFiles: [],
                SourceOperations: [Op(@"C:\src\a.dat", @"C:\src", OperationKind.Processed, 0)],
                DestinationOperations: [],
                ScanTruncated: true);

            DryRunStreamHandler handler = NewHandler(profile, new ScriptedStreamEngine(truncatedChunk), maxStreamedFiles: 500);
            List<IpcResponse> frames = await Collect(handler, profile.Id);

            Assert.Empty(DeletedKinds(frames));   // no orphan deletions previewed despite files on disk

            DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
            Assert.True(complete.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sweep_hitting_the_entry_cap_marks_the_report_truncated()
    {
        string root = Path.Combine(Path.GetTempPath(), "fm-drs-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            for (int i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(target, $"orphan{i}.txt"), "x");

            Profile profile = TestProfiles.Valid(source, target) with { SyncMode = SyncMode.Mirror };

            // A single source file keeps the source phase under the cap, so it is the sweep that trips
            // the bound: sweepBudget == MaxStreamedFiles (3) - destinationCount (0) < the 5 orphans.
            DryRunChunk chunk = new(
                SourceFiles: [Phys(@"C:\src\a.dat", @"C:\src")],
                DestinationFiles: [],
                SourceOperations: [Op(@"C:\src\a.dat", @"C:\src", OperationKind.Processed, 0)],
                DestinationOperations: []);

            DryRunStreamHandler handler = NewHandler(profile, new ScriptedStreamEngine(chunk), maxStreamedFiles: 3);
            List<IpcResponse> frames = await Collect(handler, profile.Id);

            Assert.Equal(3, DeletedKinds(frames).Count);   // capped at the sweep budget
            DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
            Assert.True(complete.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Interleaved progress frames ──────────────────────────────────────────────────────────────

    /// <summary>Holds the stream's first advance open long enough for the handler's progress poll
    /// (100 ms) to observe the source counter moving, mimicking the real engine (whose whole scan
    /// runs inside the first MoveNextAsync), then yields one chunk.</summary>
    private sealed class SlowScanEngine : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Profile profile, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
            Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < 3; i++)
            {
                progress?.SourceDiscovered();
                await Task.Delay(150, ct);
            }
            yield return Result<DryRunChunk, string>.Success(new DryRunChunk(
                [Phys(@"C:\src\a.dat", @"C:\src")], [],
                [Op(@"C:\src\a.dat", @"C:\src", OperationKind.Processed, 0)], []));
        }
    }

    [Fact]
    public async Task Interleaves_throttled_progress_frames_without_disturbing_the_report()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new SlowScanEngine(), maxStreamedFiles: 500);

        List<IpcResponse> frames = await Collect(handler, profile.Id);

        // A live scan count reached the stream before the first data chunk.
        int firstChunk = frames.FindIndex(f => f is DryRunChunkResponse);
        int firstScan = frames.FindIndex(f => f is DryRunProgressResponse { Phase: DryRunProgressPhase.ScanningSources });
        Assert.True(firstScan >= 0, "expected at least one ScanningSources progress frame");
        Assert.True(firstChunk >= 0 && firstScan < firstChunk, "scan progress must precede the first chunk");
        Assert.True(((DryRunProgressResponse)frames[firstScan]).SourceFiles > 0);

        // Exactly one BuildingLists frame, carrying the final discovery counts.
        DryRunProgressResponse building = Assert.Single(
            frames.OfType<DryRunProgressResponse>().Where(p => p.Phase == DryRunProgressPhase.BuildingLists));
        Assert.Equal(3, building.SourceFiles);

        // The terminator is still last, and reassembly sees the same single-file report.
        Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.Equal(1, frames.OfType<DryRunChunkResponse>().Sum(c => c.SourceFiles.Count));
    }
}
