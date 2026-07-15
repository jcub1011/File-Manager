using FileManager.Contracts;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace FileManager.Core.Tests.DryRun;

/// <summary>Pipeline-specific behavior of the fused scan → evaluation phases: overlap, teardown,
/// fault/cancellation semantics and determinism, driven through a scripted scanner so the scan's
/// pacing and fault injection are exact. The report-shape and policy tests live in
/// <see cref="DryRunEngineTests"/>.</summary>
public sealed class DryRunPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _target;

    public DryRunPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-pipeline-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ----- overlap -----

    [Fact]
    public async Task Batched_evaluation_overlaps_the_scan()
    {
        SourceFile("a.txt", "same!");
        TargetFile("a.txt", "diffr");   // same length, different content → evaluation hashes
        SourceFile("b.txt", "later");
        Profile profile = ProfileUnderTest();

        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedScanner scanner = new(() => BlockingScript(profile, released));
        SignalingHasher hasher = new(() => released.TrySetResult());

        var simulated = await NewEngine(scanner, profile, hasher).SimulateAsync(profile.Id, null);

        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        Assert.Equal(2, report!.SourceFiles.Count);
    }

    [Fact]
    public async Task Streaming_evaluation_overlaps_the_scan()
    {
        SourceFile("a.txt", "same!");
        TargetFile("a.txt", "diffr");
        SourceFile("b.txt", "later");
        Profile profile = ProfileUnderTest();

        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedScanner scanner = new(() => BlockingScript(profile, released));
        SignalingHasher hasher = new(() => released.TrySetResult());

        int sourceFiles = 0;
        await foreach (var item in NewEngine(scanner, profile, hasher).SimulateStreamAsync(profile.Id, null))
        {
            Assert.True(item.TryGetValue(out DryRunChunk? chunk));
            sourceFiles += chunk!.SourceFiles.Count;
        }
        Assert.Equal(2, sourceFiles);
    }

    /// <summary>The scan refuses to finish until an evaluation hash has run. Phased (scan fully
    /// drained before evaluation starts) this can never happen — the Wait times out and fails the
    /// run; pipelined, payload "a" is evaluated while the scan is still mid-enumeration.</summary>
    private IEnumerable<Result<Payload, EnumerationFault>> BlockingScript(
        Profile profile, TaskCompletionSource released)
    {
        yield return PayloadFor(profile, "a.txt");
        Assert.True(released.Task.Wait(TimeSpan.FromSeconds(30)),
            "evaluation never started while the scan was still running — the phases are not overlapping");
        yield return PayloadFor(profile, "b.txt");
    }

    // ----- truncation -----

    [Fact]
    public async Task Batched_file_cap_keeps_a_sorted_prefix_of_the_accepted_payloads()
    {
        string[] emissionOrder = ["e.txt", "a.txt", "d.txt", "b.txt", "f.txt", "c.txt", "h.txt", "g.txt"];
        foreach (string name in emissionOrder)
            SourceFile(name);
        TargetFile("orphan.txt");   // must NOT be swept into a truncated report
        Profile profile = ProfileUnderTest();
        ScriptedScanner scanner = new(() => emissionOrder.Select(n =>
            (Result<Payload, EnumerationFault>)PayloadFor(profile, n)));

        var simulated = await NewEngine(scanner, profile, maxBatchCandidates: 5).SimulateAsync(profile.Id, null);

        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        Assert.True(report!.Truncated);
        // The accepted set is the first five in emission order; the report is that set sorted.
        List<string> expected = [.. emissionOrder.Take(5).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(expected, report.SourceFiles.Select(f => f.FileName).ToList());
        AssertIndicesValid(report);
        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        Assert.DoesNotContain(report.DestinationOperations,
            o => PathOf(dirPaths, o).EndsWith("orphan.txt", StringComparison.OrdinalIgnoreCase));
    }

    // ----- faults -----

    [Fact]
    public async Task Fatal_scan_fault_fails_the_batched_run_and_tears_the_scan_down()
    {
        SourceFile("a.txt");
        Profile profile = ProfileUnderTest();
        ScriptedScanner scanner = new(() => FatalScript(profile));

        var simulated = await NewEngine(scanner, profile).SimulateAsync(profile.Id, null);

        Assert.True(simulated.TryGetError(out string? error));
        Assert.Equal("scan failed: boom", error);
        Assert.True(scanner.Disposed);
    }

    [Fact]
    public async Task Fatal_scan_fault_ends_the_stream_with_a_single_failure()
    {
        SourceFile("a.txt");
        Profile profile = ProfileUnderTest();
        ScriptedScanner scanner = new(() => FatalScript(profile));

        List<Result<DryRunChunk, string>> items = [];
        await foreach (var item in NewEngine(scanner, profile).SimulateStreamAsync(profile.Id, null))
            items.Add(item);

        Result<DryRunChunk, string> only = Assert.Single(items);
        Assert.True(only.TryGetError(out string? error));
        Assert.Equal("scan failed: boom", error);
        Assert.True(scanner.Disposed);
    }

    private IEnumerable<Result<Payload, EnumerationFault>> FatalScript(Profile profile)
    {
        yield return PayloadFor(profile, "a.txt");
        yield return new EnumerationFault("boom", EnumerationSeverity.Fatal);
        yield return PayloadFor(profile, "never.txt");   // must never be pulled
    }

    [Fact]
    public async Task Warning_fault_is_skipped_and_the_run_completes()
    {
        SourceFile("a.txt");
        SourceFile("b.txt");
        Profile profile = ProfileUnderTest();
        ScriptedScanner scanner = new(() => WarningScript(profile));

        var simulated = await NewEngine(scanner, profile).SimulateAsync(profile.Id, null);

        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        Assert.False(report!.Truncated);
        Assert.Equal(2, report.SourceFiles.Count);
    }

    private IEnumerable<Result<Payload, EnumerationFault>> WarningScript(Profile profile)
    {
        yield return PayloadFor(profile, "a.txt");
        yield return new EnumerationFault("transient", EnumerationSeverity.Warning);
        yield return PayloadFor(profile, "b.txt");
    }

    // ----- cancellation -----

    [Fact]
    public async Task Cancellation_during_evaluation_yields_canceled_and_tears_the_scan_down()
    {
        SourceFile("a.txt", "same!");
        TargetFile("a.txt", "diffr");   // hash path → the hasher is where the cancel fires
        Profile profile = ProfileUnderTest();
        using CancellationTokenSource cts = new();
        ScriptedScanner scanner = new(() => [PayloadFor(profile, "a.txt")]);
        CancellingHasher hasher = new(cts);

        var simulated = await NewEngine(scanner, profile, hasher).SimulateAsync(profile.Id, null, cts.Token);

        Assert.True(simulated.IsCanceled);
        Assert.True(scanner.Disposed);
    }

    [Fact]
    public async Task Cancellation_during_evaluation_throws_from_the_stream_enumerator()
    {
        SourceFile("a.txt", "same!");
        TargetFile("a.txt", "diffr");
        Profile profile = ProfileUnderTest();
        using CancellationTokenSource cts = new();
        ScriptedScanner scanner = new(() => [PayloadFor(profile, "a.txt")]);
        CancellingHasher hasher = new(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in NewEngine(scanner, profile, hasher).SimulateStreamAsync(profile.Id, null, cts.Token))
            {
            }
        });
        Assert.True(scanner.Disposed);
    }

    // ----- determinism -----

    [Fact]
    public async Task Reports_are_byte_identical_across_worker_counts()
    {
        string[] emissionOrder = ["j.txt", "c.txt", "a.txt", "h.txt", "e.txt", "b.txt", "l.txt",
            "d.txt", "k.txt", "f.txt", "i.txt", "g.txt"];
        foreach (string name in emissionOrder)
            SourceFile(name, $"content of {name}");
        TargetFile("c.txt", "content of c.txt");    // unchanged skip
        TargetFile("e.txt", "DIFFERENT of e.txt");  // same-size overwrite conflict (Skip policy)
        Profile profile = ProfileUnderTest();

        byte[] one = await SerializedReport(profile, manualWorkers: 1, emissionOrder);
        byte[] eight = await SerializedReport(profile, manualWorkers: 8, emissionOrder);

        Assert.Equal(one, eight);
    }

    [Fact]
    public async Task Concurrent_hashing_yields_the_expected_operation_kinds()
    {
        // Byte-identity (above) proves the workers agree; this pins the *correct* outcome so a
        // wrong-but-deterministic evaluation bug (which would agree at both worker counts and pass
        // the identity test) is still caught. Run at 8 workers so the concurrent source+target hash
        // path in the unchanged/conflict check is genuinely exercised.
        string[] emissionOrder = ["j.txt", "c.txt", "a.txt", "h.txt", "e.txt", "b.txt", "l.txt",
            "d.txt", "k.txt", "f.txt", "i.txt", "g.txt"];
        foreach (string name in emissionOrder)
            SourceFile(name, $"content of {name}");
        TargetFile("c.txt", "content of c.txt");    // byte-identical target → SkipUnchanged
        TargetFile("e.txt", "DIFFERENT of e.txt");  // same length, different content, Skip → SkipConflict
        Profile profile = ProfileUnderTest();

        DryRunReport report = await BuildReport(profile, manualWorkers: 8, emissionOrder);

        AssertIndicesValid(report);
        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        Dictionary<string, OperationKind> destKind = report.DestinationOperations
            .ToDictionary(o => o.FileName, o => o.Kind);

        Assert.Equal(12, report.SourceFiles.Count);
        Assert.Equal(OperationKind.SkipUnchanged, destKind["c.txt"]);   // hashes matched
        Assert.Equal(OperationKind.SkipConflict, destKind["e.txt"]);    // same size, hashes differ
        foreach (string name in emissionOrder.Where(n => n is not ("c.txt" or "e.txt")))
            Assert.Equal(OperationKind.New, destKind[name]);
    }

    private async Task<byte[]> SerializedReport(Profile profile, int manualWorkers, string[] emissionOrder)
    {
        DryRunReport report = await BuildReport(profile, manualWorkers, emissionOrder);
        return JsonSerializer.SerializeToUtf8Bytes(
            report with { GeneratedAt = default }, FileManagerJsonContext.Default.DryRunReport);
    }

    private async Task<DryRunReport> BuildReport(Profile profile, int manualWorkers, string[] emissionOrder)
    {
        Profile pinned = profile with
        {
            Concurrency = new ConcurrencyOverride { Mode = ConcurrencyMode.Manual, ManualWorkers = manualWorkers },
        };
        ScriptedScanner scanner = new(() => emissionOrder.Select(n =>
            (Result<Payload, EnumerationFault>)PayloadFor(pinned, n)));

        var simulated = await NewEngine(scanner, pinned).SimulateAsync(pinned.Id, null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        return report!;
    }

    // ----- scripted collaborators -----

    /// <summary>Replays a scripted scan sequence and records whether the engine disposed the
    /// iterator (the pump leaving its foreach — the teardown contract the real scanner's
    /// cancel → drain → dispose hangs off).</summary>
    private sealed class ScriptedScanner(Func<IEnumerable<Result<Payload, EnumerationFault>>> script) : ISourceScanner
    {
        public bool Disposed { get; private set; }

        public IEnumerable<Result<Payload, EnumerationFault>> Scan(
            Profile profile, TriggerKind trigger, string? scopeRoot = null,
            int? manualWorkers = null, CancellationToken ct = default)
        {
            try
            {
                foreach (var item in script())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return item;
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }

    /// <summary>Real hashing, plus a signal on the first hash call — the overlap probe.</summary>
    private sealed class SignalingHasher(Action onFirstHash) : IFileHasher
    {
        private readonly FileHasher _inner = new(NullLogger<FileHasher>.Instance);
        private int _calls;

        public Task<Result<string, JobError>> HashFileAsync(string path, VerificationMethod method, CancellationToken ct = default) =>
            _inner.HashFileAsync(path, method, ct);

        public Task<Result<byte[], JobError>> HashFileToBytesAsync(string path, VerificationMethod method, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                onFirstHash();
            return _inner.HashFileToBytesAsync(path, method, ct);
        }
    }

    /// <summary>Cancels the run from inside the first hash, the way a user cancel lands mid-evaluation.</summary>
    private sealed class CancellingHasher(CancellationTokenSource cts) : IFileHasher
    {
        public Task<Result<string, JobError>> HashFileAsync(string path, VerificationMethod method, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result<byte[], JobError>> HashFileToBytesAsync(string path, VerificationMethod method, CancellationToken ct = default)
        {
            cts.Cancel();
            return Task.FromResult(Result<byte[], JobError>.Canceled());
        }
    }

    private sealed class FakeCatalog(params Profile[] profiles) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = profiles;
        public IReadOnlyList<Profile> Active => All.Where(p => p.Active).ToList();
        public IDisposable Subscribe(Action changeHandler) => throw new NotSupportedException();
        public Result Reload() => Result.Success();
    }

    private sealed class FakeSettings(GlobalSettings current) : ISettingsProvider
    {
        public GlobalSettings Current { get; } = current;
        public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
            Result<GlobalSettings, string>.Success(settings);
    }

    // ----- plumbing -----

    private DryRunEngine NewEngine(
        ISourceScanner scanner, Profile profile, IFileHasher? hasher = null, int? maxBatchCandidates = null)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        return new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new FakeCatalog(profile),
            scanner,
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            hasher ?? new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            new FakeSettings(GlobalSettings.Default),
            TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, fileSystem, new FakeVolumeInfoProvider()))
        { MaxBatchCandidates = maxBatchCandidates ?? DryRunEngine.MaxReportedFiles };
    }

    private Profile ProfileUnderTest() => TestProfiles.Valid(sourcePath: _source, targetPath: _target);

    private Payload PayloadFor(Profile profile, string name) =>
        new(profile.Id, Path.Combine(_source, name), _source, TriggerKind.Cli, DateTimeOffset.UtcNow);

    private void SourceFile(string name, string content = "content")
        => File.WriteAllText(Path.Combine(_source, name), content);

    private void TargetFile(string name, string content = "content")
        => File.WriteAllText(Path.Combine(_target, name), content);

    /// <summary>Materializes a report file/op back to its absolute path (the wire carries
    /// (DirIndex, FileName) triples into the shared directory table, not flat strings).</summary>
    private static string PathOf(string[] dirPaths, DryRunFile file) =>
        Path.Join(dirPaths[file.DirIndex], file.FileName);

    private static string PathOf(string[] dirPaths, DryRunOperation op) =>
        Path.Join(dirPaths[op.DirIndex], op.FileName);

    private static void AssertIndicesValid(DryRunReport report)
    {
        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        foreach (DryRunOperation op in report.SourceOperations)
        {
            Assert.InRange(op.SourceIndex, 0, report.SourceFiles.Count - 1);
            Assert.Equal(-1, op.SubjectIndex);
        }
        foreach (DryRunOperation op in report.DestinationOperations)
        {
            Assert.True(op.SourceIndex == -1 || (op.SourceIndex >= 0 && op.SourceIndex < report.SourceFiles.Count),
                $"destination op SourceIndex {op.SourceIndex} out of range (SourceFiles={report.SourceFiles.Count})");
            Assert.True(op.SubjectIndex == -1 || (op.SubjectIndex >= 0 && op.SubjectIndex < report.DestinationFiles.Count),
                $"destination op SubjectIndex {op.SubjectIndex} out of range (DestinationFiles={report.DestinationFiles.Count})");
            if (op.SubjectIndex >= 0)
                Assert.Equal(PathOf(dirPaths, report.DestinationFiles[op.SubjectIndex]), PathOf(dirPaths, op));
        }
    }
}
