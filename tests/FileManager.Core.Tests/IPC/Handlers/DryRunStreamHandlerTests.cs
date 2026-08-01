using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Scanning;
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

    private static DryRunStreamHandler NewHandler(
        Profile profile, IDryRunEngine engine, int maxStreamedFiles, IEngineEventBus? eventBus = null)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, new FakeSettingsProvider());
        return new(NullLogger<DryRunStreamHandler>.Instance, engine, new FakeCatalog(profile), TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler),
            new FakeVolumeInfoProvider(), new EngineConfig(),
            eventBus ?? new EngineEventBus(NullLogger<EngineEventBus>.Instance), NullMemoryTrimCoordinator.Instance)
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
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, new FakeSettingsProvider());
        return new(NullLogger<DryRunStreamHandler>.Instance, engine, new FakeCatalog(), TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler),
            new FakeVolumeInfoProvider(), new EngineConfig(),
            new EngineEventBus(NullLogger<EngineEventBus>.Instance), NullMemoryTrimCoordinator.Instance)
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

    /// <summary>Reports the files it was told to, and bumps the shared skipped counter the way the real
    /// engine does when SourceScanner hands it a Warning-severity fault for an unreadable subdirectory.</summary>
    private sealed class SkippingStreamEngine(int totalFiles, int skipped) : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Profile profile, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
            Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < skipped; i++)
                progress?.EntrySkipped();

            List<PhysicalFile> files = Enumerable.Range(0, totalFiles)
                .Select(i => new PhysicalFile
                {
                    Path = $@"C:\x\{i}.dat",
                    Root = @"C:\x",
                    Length = 0,
                    LastWritten = DateTimeOffset.UnixEpoch,
                })
                .ToList();
            List<VirtualFileOperation> ops = Enumerable.Range(0, totalFiles)
                .Select(i => new VirtualFileOperation
                {
                    Path = $@"C:\x\{i}.dat",
                    Root = @"C:\x",
                    Kind = OperationKind.Processed,
                    SourceIndex = i,
                })
                .ToList();
            yield return Result<DryRunChunk, string>.Success(new DryRunChunk(files, [], ops, []));
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Unreadable_directories_are_reported_as_an_engine_warning()
    {
        // The preview otherwise looks complete: DryRunCompleteResponse is frozen and has no skipped
        // count, so a plan built over a partial tree is indistinguishable from one over the whole tree.
        Profile profile = TestProfiles.Valid();
        EngineEventBus bus = new(NullLogger<EngineEventBus>.Instance);
        List<EngineEvent> events = [];
        using IDisposable sub = bus.Subscribe(events.Add);
        DryRunStreamHandler handler = NewHandler(
            profile, new SkippingStreamEngine(totalFiles: 4, skipped: 3), maxStreamedFiles: 500, eventBus: bus);

        List<IpcResponse> frames = await Collect(handler, profile.Id);

        EngineWarningEvent warning = Assert.Single(events.OfType<EngineWarningEvent>());
        Assert.Contains("3 item(s)", warning.Message);
        Assert.Contains("could not be read", warning.Message);
        // The frame sequence is unchanged: the warning rides the event bus, not the response stream.
        Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.Equal(4, frames.OfType<DryRunChunkResponse>().Sum(c => c.SourceFiles.Count));
    }

    [Fact]
    public async Task No_engine_warning_when_nothing_was_skipped()
    {
        Profile profile = TestProfiles.Valid();
        EngineEventBus bus = new(NullLogger<EngineEventBus>.Instance);
        List<EngineEvent> events = [];
        using IDisposable sub = bus.Subscribe(events.Add);
        DryRunStreamHandler handler = NewHandler(
            profile, new SkippingStreamEngine(totalFiles: 4, skipped: 0), maxStreamedFiles: 500, eventBus: bus);

        await Collect(handler, profile.Id);

        Assert.Empty(events.OfType<EngineWarningEvent>());
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

    // ── Teardown when the server abandons the stream ────────────────────────────────────────────
    // The IPC server's failure mode: a frame write fails (the client disconnected) and it disposes
    // this handler's enumerator WITHOUT cancelling the token — that token is the server's own and
    // only fires at shutdown. Teardown must complete promptly from either suspension the handler can
    // be abandoned at: a data-chunk yield (the sweep's producer is parked on its bounded channel) or
    // an interleaved progress yield (the next sweep advance is still in flight by construction).

    /// <summary>Yields <paramref name="filesPerRoot"/> synthetic files under each submitted root, so
    /// a multi-chunk sweep costs no disk. Honours the session's <c>OnFile</c> policy the way the real
    /// scheduler does.</summary>
    private sealed class SyntheticScanScheduler(int filesPerRoot) : IScanScheduler
    {
        public IScanSession OpenSession(ScanSessionOptions options, CancellationToken ct) =>
            new Session(options, filesPerRoot, ct);

        private sealed class Session(ScanSessionOptions options, int filesPerRoot, CancellationToken ct) : IScanSession
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
                            FileName: $"f{i:D8}.dat",
                            FullPath: Path.Combine(root.Directory, $"f{i:D8}.dat"),
                            IsDirectory: false,
                            Size: 0,
                            Modified: DateTimeOffset.UnixEpoch);
                        if (!options.OnFile(entry, root.Tag))
                            continue;
                        yield return new ScanResult(entry, null, root.Tag);
                    }
                }
            }

            public void Dispose() { }
        }
    }

    /// <summary>A walk that never produces and never returns until the session's token is cancelled —
    /// the "sweep advance pending" state frozen, so a test can abandon the handler exactly there.</summary>
    private sealed class BlockedScanScheduler : IScanScheduler
    {
        public IScanSession OpenSession(ScanSessionOptions options, CancellationToken ct) => new Session(ct);

        private sealed class Session(CancellationToken ct) : IScanSession
        {
            public void Submit(ScanWorkItem item) { }

            public void CompleteSubmissions() { }

            public IEnumerable<ScanResult> Consume()
            {
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
                yield break;
            }

            public void Dispose() { }
        }
    }

    private static DryRunStreamHandler NewHandler(Profile profile, IDryRunEngine engine, IScanScheduler scheduler) =>
        new(NullLogger<DryRunStreamHandler>.Instance, engine, new FakeCatalog(profile), TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler),
            new FakeVolumeInfoProvider(), new EngineConfig(),
            new EngineEventBus(NullLogger<EngineEventBus>.Instance), NullMemoryTrimCoordinator.Instance)
        { MaxStreamedFiles = 1_000_000 };

    [Fact]
    public async Task Teardown_completes_when_the_stream_is_abandoned_mid_sweep()
    {
        // 20,000 swept entries at the production chunk budget is dozens of chunks, so after the first
        // sweep data frame the producer is still mid-stream — parked on its bounded channel. Without
        // the projector cancelling its own producer on teardown, this dispose hangs until the token
        // fires (for the real server: service shutdown).
        Profile profile = TestProfiles.Valid() with { SyncMode = SyncMode.Mirror, ScanDestination = true };
        DryRunStreamHandler handler = NewHandler(
            profile, new FakeStreamEngine(totalFiles: 8, chunkSize: 4),
            new SyntheticScanScheduler(filesPerRoot: 20_000));

        IAsyncEnumerator<IpcResponse> frames = handler
            .HandleStreamAsync(new DryRunStreamRequest { ProfileId = profile.Id })
            .GetAsyncEnumerator(CancellationToken.None);

        bool sawSweepChunk = false;
        while (!sawSweepChunk)
        {
            Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)),
                "the stream ended before the sweep emitted a data chunk");
            sawSweepChunk = frames.Current is DryRunChunkResponse { DestinationOperations.Count: > 0 };
        }

        await frames.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Teardown_completes_when_the_stream_is_abandoned_while_a_sweep_advance_is_pending()
    {
        // Disposing an async iterator with a MoveNextAsync still in flight throws, so the handler
        // must observe (cancel + await) the pending advance before its await-using disposes the sweep
        // enumerator. A walk that never returns keeps the advance pending deterministically.
        Profile profile = TestProfiles.Valid() with { SyncMode = SyncMode.Mirror, ScanDestination = true };
        DryRunStreamHandler handler = NewHandler(
            profile, new FakeStreamEngine(totalFiles: 8, chunkSize: 4), new BlockedScanScheduler());

        IAsyncEnumerator<IpcResponse> frames = handler
            .HandleStreamAsync(new DryRunStreamRequest { ProfileId = profile.Id })
            .GetAsyncEnumerator(CancellationToken.None);

        // The first SweepingDestinations frame is unconditional (yielded before the walk starts); the
        // SECOND comes from the throttled poll, yielded while the advance is pending — the suspension
        // under test.
        int sweepProgressFrames = 0;
        while (sweepProgressFrames < 2)
        {
            Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)),
                "the stream ended before the sweep phase began");
            if (frames.Current is DryRunProgressResponse { Phase: DryRunProgressPhase.SweepingDestinations })
                sweepProgressFrames++;
        }

        await frames.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
