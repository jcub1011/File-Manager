using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Contracts.Settings;
using FileManager.Core.Placement;
using FileManager.Core.Scanning;
using FileManager.Core.Settings;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.DryRun;

public sealed class DryRunEngineTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _target;

    public DryRunEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-dryrun-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ----- hash-worker resolution -----

    private static readonly int ExpectedAutoHashWorkers = Math.Max(1, Environment.ProcessorCount - 1);

    private static GlobalSettings WithHashThreads(ThreadBudget budget) =>
        GlobalSettings.Default with { ScanThreading = new ScanThreadingSettings { MaxHashThreads = budget } };

    [Fact]
    public void ResolveWorkers_uses_the_auto_hash_formula_by_default()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, GlobalSettings.Default);
        Assert.Equal(ExpectedAutoHashWorkers, engine.ResolveWorkers());
    }

    [Fact]
    public void ResolveWorkers_honors_an_explicit_hash_thread_pin()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, WithHashThreads(ThreadBudget.Explicit(5)));
        Assert.Equal(5, engine.ResolveWorkers());
    }

    [Fact]
    public void ResolveWorkers_clamps_a_hash_thread_pin_below_one()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, WithHashThreads(ThreadBudget.Explicit(0)));
        Assert.Equal(1, engine.ResolveWorkers());
    }

    private static DryRunEngine NewEngine() =>
        NewEngine(DryRunEngine.MaxReportBytes, GlobalSettings.Default);

    private static DryRunEngine NewEngine(int reportByteBudget) =>
        NewEngine(reportByteBudget, GlobalSettings.Default);

    private static DryRunEngine NewEngine(int reportByteBudget, GlobalSettings global) =>
        NewEngine(reportByteBudget, DryRunEngine.WireChunkByteBudget, global);

    private static DryRunEngine NewEngine(int reportByteBudget, int chunkByteBudget, GlobalSettings global) =>
        NewEngine(reportByteBudget, chunkByteBudget, DryRunEngine.MaxStreamedFiles, global);

    /// <summary>Shrinks the batched path's candidate cap, which also bounds its destination sweep.</summary>
    private static DryRunEngine NewEngineWithBatchCap(int maxBatchCandidates) =>
        NewEngine(
            DryRunEngine.MaxReportBytes, DryRunEngine.WireChunkByteBudget, DryRunEngine.MaxStreamedFiles,
            GlobalSettings.Default, maxBatchCandidates);

    private static DryRunEngine NewEngine(
        int reportByteBudget, int chunkByteBudget, int maxScannedCandidates, GlobalSettings global) =>
        NewEngine(reportByteBudget, chunkByteBudget, maxScannedCandidates, global, DryRunEngine.MaxReportedFiles);

    private static DryRunEngine NewEngine(
        int reportByteBudget, int chunkByteBudget, int maxScannedCandidates, GlobalSettings global,
        int maxBatchCandidates)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        FakeSettings settings = new(global);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, settings);
        return new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new SourceScanner(TimeProvider.System, scheduler),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            settings,
            TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler))
        {
            ReportByteBudget = reportByteBudget,
            ChunkByteBudget = chunkByteBudget,
            MaxScannedCandidates = maxScannedCandidates,
            MaxBatchCandidates = maxBatchCandidates,
        };
    }

    /// <summary>Concatenates a stream's source files with their source operations (paired by the
    /// global index — the position in the concatenated list, matching each op's SourceIndex).</summary>
    private static async Task<List<(string SourcePath, OperationKind Kind)>> CollectStream(
        DryRunEngine engine, Profile profile, string? scope = null, CancellationToken ct = default)
    {
        List<IPhysicalFileView> files = [];
        Dictionary<int, IFileOperationView> ops = [];
        await foreach (Result<DryRunChunk, string> chunk in engine.SimulateStreamAsync(profile, scope, ct: ct))
        {
            Assert.True(chunk.TryGetValue(out DryRunChunk? c), "stream yielded a failure chunk");
            files.AddRange(c!.SourceFiles);
            foreach (IFileOperationView op in c.SourceOperations)
                ops[op.SourceIndex] = op;
        }
        return files.Select((f, i) => (f.Path, ops[i].Kind)).ToList();
    }

    private sealed class FakeSettings(GlobalSettings current) : ISettingsProvider
    {
        public GlobalSettings Current { get; } = current;
        public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
            Result<GlobalSettings, string>.Success(settings);
    }

    private Profile ProfileUnderTest(
        ConflictResolution conflict = ConflictResolution.Skip,
        OnSuccessAction onSuccess = OnSuccessAction.KeepSource,
        VerificationMethod verification = VerificationMethod.Sha256,
        FilterSet? filters = null)
    {
        Profile profile = TestProfiles.Valid(sourcePath: _source, targetPath: _target) with { Filters = filters };
        return profile with
        {
            Policies = profile.Policies with
            {
                ConflictResolution = conflict,
                OnSuccess = onSuccess,
                VerificationMethod = verification,
            },
        };
    }

    private async Task<DryRunReport> Simulate(Profile profile, string? scope = null)
    {
        var simulated = await NewEngine().SimulateAsync(profile, scope);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        AssertIndicesValid(report!);
        return report;
    }

    // ----- helpers over the new report shape -----

    /// <summary>Materializes the report's directory table and Path.Joins a file/op back to its
    /// absolute path (the wire carries (DirIndex, FileName) triples, not flat strings).</summary>
    private static string PathOf(string[] dirPaths, DryRunFile file) =>
        Path.Join(dirPaths[file.DirIndex], file.FileName);

    private static string PathOf(string[] dirPaths, DryRunOperation op) =>
        Path.Join(dirPaths[op.DirIndex], op.FileName);

    private static string PathOf(DryRunReport report, DryRunFile file) =>
        PathOf(DryRunDirectoryTable.Materialize(report.Directories), file);

    private static string PathOf(DryRunReport report, DryRunOperation op) =>
        PathOf(DryRunDirectoryTable.Materialize(report.Directories), op);

    private static string RootOf(DryRunReport report, DryRunFile file) =>
        DryRunDirectoryTable.Materialize(report.Directories)[file.RootDirIndex];

    private static DryRunOperation SourceOpFor(DryRunReport report, string sourcePathSuffix)
    {
        int index = IndexOfSource(report, sourcePathSuffix);
        return report.SourceOperations.Single(o => o.SourceIndex == index);
    }

    private static IReadOnlyList<DryRunOperation> DestOpsForSource(DryRunReport report, string sourcePathSuffix)
    {
        int index = IndexOfSource(report, sourcePathSuffix);
        return report.DestinationOperations.Where(o => o.SourceIndex == index).ToList();
    }

    private static int IndexOfSource(DryRunReport report, string sourcePathSuffix)
    {
        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        for (int i = 0; i < report.SourceFiles.Count; i++)
            if (PathOf(dirPaths, report.SourceFiles[i]).EndsWith(sourcePathSuffix, StringComparison.OrdinalIgnoreCase))
                return i;
        Assert.Fail($"no source file ending in '{sourcePathSuffix}'");
        return -1;
    }

    /// <summary>Referential-integrity invariant: every operation index is either -1 or a valid
    /// position in the correct list, and an in-place subject's path matches the op's path.</summary>
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

    private void SourceFile(string name, string content = "content")
    {
        string path = Path.Combine(_source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void TargetFile(string name, string content = "content")
    {
        string path = Path.Combine(_target, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Fresh_file_would_write()
    {
        SourceFile("new.txt");
        DryRunReport report = await Simulate(ProfileUnderTest());

        DryRunFile file = Assert.Single(report.SourceFiles);
        Assert.EndsWith("source", RootOf(report, file), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationKind.Processed, Assert.Single(report.SourceOperations).Kind);

        DryRunOperation action = Assert.Single(report.DestinationOperations);
        Assert.Equal(OperationKind.New, action.Kind);
        Assert.Equal(Path.Combine(_target, "new.txt"), PathOf(report, action));
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task Each_result_is_stamped_with_its_originating_source_root()
    {
        // Two sources, one file each: every source file must carry the root it was enumerated under so
        // the GUI can group/filter a multi-source report by source.
        string sourceB = Path.Combine(_root, "source-b");
        Directory.CreateDirectory(sourceB);
        File.WriteAllText(Path.Combine(_source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(sourceB, "b.txt"), "b");

        Profile profile = ProfileUnderTest() with
        {
            Sources = [new SourceConfig { Path = _source }, new SourceConfig { Path = sourceB }],
        };

        DryRunReport report = await Simulate(profile);

        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        DryRunFile a = Assert.Single(report.SourceFiles, f => PathOf(dirPaths, f).EndsWith("a.txt"));
        DryRunFile b = Assert.Single(report.SourceFiles, f => PathOf(dirPaths, f).EndsWith("b.txt"));
        Assert.EndsWith("source", dirPaths[a.RootDirIndex], StringComparison.OrdinalIgnoreCase);      // ...\source
        Assert.EndsWith("source-b", dirPaths[b.RootDirIndex], StringComparison.OrdinalIgnoreCase);    // ...\source-b
        Assert.NotEqual(dirPaths[a.RootDirIndex], dirPaths[b.RootDirIndex]);
    }

    [Fact]
    public async Task Report_truncates_at_the_byte_budget_and_the_serialized_response_fits()
    {
        // Sized so the first unit fits but all 20 never do: under the directory-table wire shape the
        // first bundle also pays for the whole ancestor chain of the (deep, machine-dependent) temp
        // root — roughly a dozen table entries — before the flat per-file marginal cost kicks in.
        const int budget = 6144;
        for (int i = 0; i < 20; i++)
            // Non-ASCII name: JSON escaping (non-ASCII counts 6-byte \uXXXX worst case) must count
            // against the budget in the file/op records.
            SourceFile(Path.Combine("nested", "further", $"a-quite-long-file-name résumé ✓ {i:D4}.txt"), $"content {i}");
        Profile profile = ProfileUnderTest();

        var simulated = await NewEngine(budget).SimulateAsync(profile, null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));

        Assert.True(report!.Truncated);
        Assert.InRange(report.SourceFiles.Count, 1, 19);
        AssertIndicesValid(report);

        // The measurement is a true upper bound: the full wire response is the measured records plus
        // commas and a bounded envelope.
        int wireBytes = IpcSerializer.SerializeResponse(new DryRunResponse { Report = report }).Length;
        Assert.True(wireBytes <= budget + 512,
            $"serialized response was {wireBytes} bytes for a {budget}-byte report budget");
    }

    [Fact]
    public async Task Report_files_are_ordered_by_source_path_regardless_of_evaluation_order()
    {
        // Creation order is deliberately not sorted order; the parallel, batched evaluation may
        // complete files in any order, but the report must be a stable ascending-by-source-path list.
        string[] names = ["m.txt", "a.txt", "z.txt", "c.txt", "b.txt", "y.txt", "d.txt", "n.txt"];
        foreach (string name in names)
            SourceFile(name, $"content of {name}");

        DryRunReport report = await Simulate(ProfileUnderTest());

        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        List<string> paths = report.SourceFiles.Select(f => PathOf(dirPaths, f)).ToList();
        Assert.Equal(names.Length, paths.Count);
        Assert.Equal(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), paths);
    }

    [Fact]
    public async Task Truncation_keeps_the_lexicographically_first_files()
    {
        // Zero-padded names so lexical order equals numeric order; a budget small enough to force
        // truncation must keep exactly the lexicographically-first prefix (the early-halt property).
        for (int i = 0; i < 40; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        // The tight estimator sits close to the true serialized size, so this budget keeps a
        // couple of ~110-char-path bundles (~1.6 KB each) before truncating.
        var simulated = await NewEngine(4000).SimulateAsync(profile, null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));

        Assert.True(report!.Truncated);
        Assert.InRange(report.SourceFiles.Count, 1, 39);
        AssertIndicesValid(report);

        List<string> kept = report.SourceFiles.Select(f => f.FileName).ToList();
        List<string> expectedPrefix = Enumerable.Range(0, 40).Select(i => $"{i:D3}.txt")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(kept.Count).ToList();
        Assert.Equal(expectedPrefix, kept);
    }

    [Fact]
    public async Task Filtered_file_reports_the_deciding_filter()
    {
        SourceFile("song.wav");
        SourceFile("notes.txt");
        DryRunReport report = await Simulate(ProfileUnderTest(filters: new FilterSet { Include = ["*.wav"] }));

        DryRunOperation skipped = SourceOpFor(report, "notes.txt");
        Assert.Equal(OperationKind.SkippedByFilter, skipped.Kind);
        Assert.Contains("*.wav", skipped.Detail);
        Assert.Empty(DestOpsForSource(report, "notes.txt"));   // a filtered file writes nowhere

        Assert.Equal(OperationKind.Processed, SourceOpFor(report, "song.wav").Kind);
    }

    [Fact]
    public async Task Identical_existing_target_is_an_unchanged_skip_and_suppresses_disposition()
    {
        SourceFile("same.txt", "identical bytes");
        TargetFile("same.txt", "identical bytes");
        DryRunReport report = await Simulate(ProfileUnderTest(onSuccess: OnSuccessAction.MoveToTrash));

        DryRunOperation sourceOp = Assert.Single(report.SourceOperations);
        Assert.Equal(OperationKind.SkippedUnchanged, sourceOp.Kind);
        Assert.Equal(OperationKind.SkipUnchanged, Assert.Single(report.DestinationOperations).Kind);
        // §3.4.1: an all-unchanged job closes Skipped without committing — no disposition runs.
        Assert.Null(sourceOp.SourceDisposition);
    }

    [Fact]
    public async Task Same_size_different_content_is_not_unchanged()
    {
        SourceFile("clash.txt", "AAAA");
        TargetFile("clash.txt", "BBBB");   // same length, different bytes
        DryRunReport report = await Simulate(ProfileUnderTest(conflict: ConflictResolution.Overwrite));

        Assert.Equal(OperationKind.Overwrite, Assert.Single(report.DestinationOperations).Kind);
    }

    [Fact]
    public async Task RenameSuffix_predicts_the_suffixed_name()
    {
        SourceFile("dup.txt", "new content");
        TargetFile("dup.txt", "old");
        DryRunReport report = await Simulate(ProfileUnderTest(conflict: ConflictResolution.RenameSuffix));

        // A rename emits a Rename op at the suffixed path (a target of the source) and an Untouched op
        // for the kept original (destination-only, SourceIndex == -1).
        DryRunOperation rename = Assert.Single(report.DestinationOperations, o => o.Kind == OperationKind.Rename);
        Assert.Equal(Path.Combine(_target, "dup (1).txt"), PathOf(report, rename));
        Assert.Equal(0, rename.SourceIndex);   // single source file → index 0

        DryRunOperation kept = Assert.Single(report.DestinationOperations, o => o.Kind == OperationKind.Untouched);
        Assert.Equal(Path.Combine(_target, "dup.txt"), PathOf(report, kept));
        Assert.Equal(-1, kept.SourceIndex);

        // Only the rename is a target of the source; the kept original is not.
        Assert.Equal([OperationKind.Rename], DestOpsForSource(report, "dup.txt").Select(o => o.Kind));
    }

    [Fact]
    public async Task Skip_policy_reports_a_conflict_skip_but_still_processes()
    {
        SourceFile("kept.txt", "new content longer");
        TargetFile("kept.txt", "old");
        DryRunReport report = await Simulate(ProfileUnderTest(conflict: ConflictResolution.Skip,
            onSuccess: OnSuccessAction.MoveToTrash));

        Assert.Equal(OperationKind.SkipConflict, Assert.Single(report.DestinationOperations).Kind);
        DryRunOperation sourceOp = Assert.Single(report.SourceOperations);
        Assert.Equal(OperationKind.Processed, sourceOp.Kind);
        Assert.Equal(OnSuccessAction.MoveToTrash, sourceOp.SourceDisposition);
    }

    [Fact]
    public async Task OverwriteIfNewer_goes_both_ways()
    {
        SourceFile("timed.txt", "newer source content!");
        TargetFile("timed.txt", "old");
        // Make the existing target NEWER than the source → skip; then OLDER → overwrite.
        File.SetLastWriteTimeUtc(Path.Combine(_target, "timed.txt"), DateTime.UtcNow.AddHours(1));
        DryRunReport newerTarget = await Simulate(ProfileUnderTest(conflict: ConflictResolution.OverwriteIfNewer));
        Assert.Equal(OperationKind.SkipConflict, Assert.Single(newerTarget.DestinationOperations).Kind);

        File.SetLastWriteTimeUtc(Path.Combine(_target, "timed.txt"), DateTime.UtcNow.AddHours(-1));
        DryRunReport olderTarget = await Simulate(ProfileUnderTest(conflict: ConflictResolution.OverwriteIfNewer));
        Assert.Equal(OperationKind.Overwrite, Assert.Single(olderTarget.DestinationOperations).Kind);
    }

    [Fact]
    public async Task PreserveStructure_keeps_relative_paths()
    {
        SourceFile(Path.Combine("nested", "deep", "file.txt"));
        DryRunReport report = await Simulate(ProfileUnderTest());

        DryRunOperation action = Assert.Single(report.DestinationOperations);
        Assert.Equal(Path.Combine(_target, "nested", "deep", "file.txt"), PathOf(report, action));
    }

    /// <summary>The frame-fit guarantee rests on the estimate being a TRUE upper bound of what the
    /// serializer emits, for any string content — ASCII paths, escape-heavy names, non-ASCII,
    /// control chars, surrogate pairs. The serializer here uses the same default encoder as
    /// <c>FileManagerJsonContext</c> (no custom encoder configured).</summary>
    [Theory]
    [InlineData(@"C:\Users\Jacob McCormack\source\repos\File-Manager\file.txt")]
    [InlineData("plain-ascii_name.txt (1)")]
    [InlineData("quotes \" and \\ backslashes \\\\ everywhere \"\"")]
    [InlineData("résumé ✓ naïve Größe 日本語")]
    [InlineData("emoji 🎉🚀 surrogate pairs 𝔘𝔫𝔦")]
    [InlineData("controlchars\ttabs\nnewlines\r")]
    [InlineData("html-sensitive <tag> & 'quote' + `tick`")]
    [InlineData("")]
    public void String_upper_bound_never_underestimates_the_serialized_size(string value)
    {
        long bound = DryRunEngine.StringUpperBound(value);
        int actual = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value).Length;
        Assert.True(bound >= actual, $"estimate {bound} < serialized {actual} for \"{value}\"");
    }

    [Fact]
    public async Task Profile_overload_previews_a_draft_absent_from_the_catalog()
    {
        SourceFile("a.txt");
        SourceFile("b.txt");
        Profile draft = ProfileUnderTest();

        // The engine previews the profile object directly, exactly as an unsaved in-memory draft
        // (never persisted to the catalog) would.
        var simulated = await NewEngine().SimulateAsync(draft, null);

        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        Assert.Equal(draft.Id, report!.ProfileId);
        Assert.Equal(2, report.SourceFiles.Count);
    }

    [Fact]
    public async Task Stream_Profile_overload_previews_a_draft_absent_from_the_catalog()
    {
        SourceFile("a.txt");
        Profile draft = ProfileUnderTest();

        int sourceFiles = 0;
        await foreach (Result<DryRunChunk, string> item in NewEngine().SimulateStreamAsync(draft, null))
        {
            Assert.True(item.TryGetValue(out DryRunChunk? chunk));
            sourceFiles += chunk!.SourceFiles.Count;
        }
        Assert.Equal(1, sourceFiles);
    }

    [Fact]
    public async Task Cancellation_yields_a_Canceled_result_not_an_exception()
    {
        SourceFile("a.txt");
        SourceFile("b.txt");
        Profile profile = ProfileUnderTest();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        var simulated = await NewEngine().SimulateAsync(profile, null, cts.Token);

        Assert.True(simulated.IsCanceled);
        Assert.False(simulated.TryGetValue(out _));
        Assert.False(simulated.TryGetError(out _));
    }

    [Fact]
    public async Task Dry_run_performs_zero_filesystem_mutation()
    {
        // The I-DRYRUN-RO proof: a scenario touching every code path — hashing an existing
        // identical target, probing overwrite and rename-suffix candidates, filter skips,
        // and a disposing OnSuccess — then a full before/after tree comparison.
        SourceFile("same.txt", "identical bytes");
        TargetFile("same.txt", "identical bytes");
        SourceFile("clash.txt", "AAAA");
        TargetFile("clash.txt", "BBBB");
        SourceFile("fresh.txt", "brand new");
        SourceFile("skipme.tmp", "junk");
        SourceFile(Path.Combine("nested", "inner.txt"), "nested");

        Dictionary<string, (long Length, DateTime Mtime)> before = SnapshotTree();

        await Simulate(ProfileUnderTest(
            conflict: ConflictResolution.RenameSuffix,
            onSuccess: OnSuccessAction.PermanentDelete,
            filters: new FilterSet { ExcludeGlob = ["*.tmp"] }));

        Assert.Equal(before, SnapshotTree());
    }

    [Fact]
    public async Task Mirror_report_previews_destination_orphans_with_zero_mutation()
    {
        // A source file that lands in the target, plus a pre-existing target-only file that a real
        // Mirror run would delete. The engine wires the read-only destination sweep, so the report
        // surfaces the orphan as Deleted — and touches nothing on disk.
        SourceFile("keep.txt", "content");
        TargetFile("orphan.txt", "stale");

        Dictionary<string, (long Length, DateTime Mtime)> before = SnapshotTree();

        DryRunReport report = await Simulate(ProfileUnderTest() with { SyncMode = SyncMode.Mirror });

        DryRunOperation orphan = Assert.Single(report.DestinationOperations, o => o.Kind == OperationKind.Deleted);
        Assert.EndsWith("orphan.txt", PathOf(report, orphan));
        Assert.Equal(-1, orphan.SourceIndex);   // an orphan has no incoming source
        Assert.EndsWith("orphan.txt", PathOf(report, report.DestinationFiles[orphan.SubjectIndex]));
        Assert.Equal(before, SnapshotTree());
    }

    [Fact]
    public async Task Additive_report_previews_preexisting_targets_as_untouched()
    {
        SourceFile("keep.txt", "content");
        TargetFile("preexisting.txt", "already here");

        // AdditiveArchive with the destination scan opted in.
        DryRunReport report = await Simulate(ProfileUnderTest() with { ScanDestination = true });

        DryRunOperation entry = Assert.Single(report.DestinationOperations, o => o.Kind == OperationKind.Untouched);
        Assert.EndsWith("preexisting.txt", PathOf(report, entry));
    }

    [Fact]
    public async Task The_batched_destination_sweep_is_bounded_and_marks_the_report_truncated()
    {
        // The report's BYTE budget bounds the report, not the sweep: without a cap the projector first
        // materializes a PhysicalFile + VirtualFileOperation for every pre-existing file under every
        // target root on the service heap, and only then does the byte budget start rejecting. A target
        // that is a large existing archive could exhaust service memory — the failure the streamed path
        // was already hardened against.
        SourceFile("keep.txt", "content");
        for (int i = 0; i < 8; i++)
            TargetFile($"preexisting-{i}.txt", $"already here {i}");

        var simulated = await NewEngineWithBatchCap(3)
            .SimulateAsync(ProfileUnderTest() with { ScanDestination = true }, null);

        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        Assert.True(report!.Truncated, "a capped sweep must be reported, not silently short");
        Assert.InRange(report.DestinationFiles.Count, 1, 8);
        AssertIndicesValid(report);   // no retained operation may reference a dropped file
    }

    [Fact]
    public async Task Additive_without_scan_skips_the_destination_sweep()
    {
        SourceFile("keep.txt", "content");
        TargetFile("preexisting.txt", "already here");

        // AdditiveArchive with ScanDestination = false (the default): the destination sweep is
        // skipped, so a pre-existing target-only file produces no Untouched destination row.
        DryRunReport report = await Simulate(ProfileUnderTest() with { ScanDestination = false });

        Assert.DoesNotContain(report.DestinationOperations, o => o.Kind == OperationKind.Untouched);
        string[] dirPaths = DryRunDirectoryTable.Materialize(report.Directories);
        Assert.DoesNotContain(report.DestinationFiles,
            f => PathOf(dirPaths, f).EndsWith("preexisting.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Mirror_scans_destinations_even_when_ScanDestination_is_false()
    {
        SourceFile("keep.txt", "content");
        TargetFile("orphan.txt", "stale");

        // Safety net: Mirror needs the sweep to detect deletions, so it scans regardless of the flag.
        DryRunReport report = await Simulate(
            ProfileUnderTest() with { SyncMode = SyncMode.Mirror, ScanDestination = false });

        Assert.Single(report.DestinationOperations, o => o.Kind == OperationKind.Deleted);
    }

    // ----- referential-integrity invariants -----

    [Fact]
    public async Task Operation_indices_are_valid_across_a_mixed_run()
    {
        SourceFile("fresh.txt", "brand new");                    // New
        SourceFile("same.txt", "identical"); TargetFile("same.txt", "identical");   // SkipUnchanged
        SourceFile("dup.txt", "changed content"); TargetFile("dup.txt", "x");        // Rename (+ kept Untouched)
        SourceFile("junk.tmp", "junk");                          // filtered

        DryRunReport report = await Simulate(ProfileUnderTest(
            conflict: ConflictResolution.RenameSuffix,
            onSuccess: OnSuccessAction.MoveToTrash,
            filters: new FilterSet { ExcludeGlob = ["*.tmp"] }) with { SyncMode = SyncMode.Mirror });

        AssertIndicesValid(report);   // also runs inside Simulate; explicit here for intent
        Assert.Equal(report.SourceFiles.Count, report.SourceOperations.Count);
    }

    [Fact]
    public async Task Truncated_report_contains_no_out_of_range_indices()
    {
        for (int i = 0; i < 40; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        var simulated = await NewEngine(4000).SimulateAsync(profile, null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));

        Assert.True(report!.Truncated);
        Assert.NotEmpty(report.SourceFiles);   // a vacuously empty report would prove nothing
        AssertIndicesValid(report);   // bundle-boundary truncation ⇒ no retained op references a dropped file
    }

    // ----- streaming (SimulateStreamAsync) -----

    [Fact]
    public async Task Stream_caps_the_candidate_buffer_at_the_scan_safety_bound()
    {
        // The candidate buffer must be bounded so a pathological scan can't buffer (and hash) an
        // unbounded number of files before the first chunk. A tiny cap truncates the scan to a
        // bounded subset rather than materializing all 20.
        for (int i = 0; i < 20; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        DryRunEngine engine = NewEngine(
            DryRunEngine.MaxReportBytes, DryRunEngine.WireChunkByteBudget, maxScannedCandidates: 5,
            GlobalSettings.Default);
        List<(string SourcePath, OperationKind Kind)> streamed = await CollectStream(engine, profile);

        Assert.Equal(5, streamed.Count);
    }

    [Fact]
    public async Task Stream_yields_the_same_results_as_the_batched_report_regardless_of_order()
    {
        string[] names = ["m.txt", "a.txt", "z.txt", "c.txt", "b.txt", "y.txt", "d.txt", "n.txt"];
        foreach (string name in names)
            SourceFile(name, $"content of {name}");
        Profile profile = ProfileUnderTest();

        DryRunReport batched = await Simulate(profile);
        string[] dirPaths = DryRunDirectoryTable.Materialize(batched.Directories);
        Dictionary<int, DryRunOperation> batchedOps = batched.SourceOperations.ToDictionary(o => o.SourceIndex);
        List<(string, OperationKind)> batchedPairs =
            batched.SourceFiles.Select((f, i) => (PathOf(dirPaths, f), batchedOps[i].Kind)).ToList();

        List<(string SourcePath, OperationKind Kind)> streamed = await CollectStream(NewEngine(), profile);

        // The streamed path emits in discovery (completion) order and leaves sorting to the client,
        // so it agrees with the batched report as a SET — same files, same verdicts — not in row order.
        Assert.Equal(
            batchedPairs.OrderBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).ToList(),
            streamed.OrderBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(p => (p.SourcePath, p.Kind)).ToList());
    }

    [Fact]
    public async Task Stream_has_no_truncation_ceiling_where_the_batched_report_truncates()
    {
        // A budget that truncates the single-frame report must NOT truncate the stream: streaming
        // reports every file across many frames.
        for (int i = 0; i < 40; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        var batched = await NewEngine(4000).SimulateAsync(profile, null);
        Assert.True(batched.TryGetValue(out DryRunReport? truncatedReport));
        Assert.True(truncatedReport!.Truncated);
        Assert.InRange(truncatedReport.SourceFiles.Count, 1, 39);

        // Same tiny budget as the *chunk* budget — still every file comes back.
        List<(string SourcePath, OperationKind Kind)> streamed =
            await CollectStream(NewEngine(4000, 4000, GlobalSettings.Default), profile);
        Assert.Equal(40, streamed.Count);
        // Discovery order — sort the streamed names before comparing to the full expected set.
        Assert.Equal(
            Enumerable.Range(0, 40).Select(i => $"{i:D3}.txt").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            streamed.Select(f => Path.GetFileName(f.SourcePath)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    [Fact]
    public async Task Stream_splits_into_multiple_chunks_at_the_chunk_budget()
    {
        for (int i = 0; i < 30; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        // A small chunk budget forces several chunks; each stays under the 16 MiB frame cap by design.
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, 512, GlobalSettings.Default);
        List<DryRunChunk> chunks = [];
        await foreach (Result<DryRunChunk, string> chunk in engine.SimulateStreamAsync(profile, null))
        {
            Assert.True(chunk.TryGetValue(out DryRunChunk? batch));
            chunks.Add(batch!);
        }

        Assert.True(chunks.Count > 1, $"expected multiple chunks, got {chunks.Count}");
        Assert.All(chunks, c => Assert.NotEmpty(c.SourceFiles));
        Assert.Equal(30, chunks.Sum(c => c.SourceFiles.Count));
    }

    // ----- streaming: spool spill + carrier pooling -----

    [Fact]
    public async Task Spilled_stream_report_is_content_identical_to_the_unpooled_in_memory_run()
    {
        // A run large enough to spill to disk (SpillThresholdBytes = 1) replays through pooled,
        // mutated-in-place carriers; the in-memory spool replays the original records via `with { }`.
        // The two must assemble to the same report content — the guard against a wrong in-place index
        // remap or cross-carrier aliasing. A mix of New / SkipUnchanged / Overwrite exercises the
        // destination-file + SubjectIndex remap that source-only runs never touch.
        for (int i = 0; i < 40; i++)
        {
            SourceFile($"{i:D3}.txt", $"content {i}");
            if (i % 3 == 0) TargetFile($"{i:D3}.txt", $"content {i}");          // identical → SkipUnchanged
            else if (i % 3 == 1) TargetFile($"{i:D3}.txt", $"different {i}!!"); // → Overwrite
        }
        Profile profile = ProfileUnderTest(conflict: ConflictResolution.Overwrite);

        string scratch = Path.Combine(_root, "spill-identical");
        AssembledStream pooled = await AssembleStream(SpillingEngine(scratch, chunkByteBudget: 4000, pool: null), profile);
        AssembledStream unpooled = await AssembleStream(InMemoryStreamEngine(chunkByteBudget: 4000), profile);

        AssertSameContent(unpooled, pooled);
        // Sanity: this really did stream a non-trivial report through the spill path.
        Assert.Equal(40, pooled.SourceFiles.Count);
    }

    [Fact]
    public async Task Spilled_stream_returns_every_rented_carrier_to_the_pool()
    {
        for (int i = 0; i < 60; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        // Tiny chunk budget → one file per chunk → many recycle cycles across the run.
        EvaluationCarrierPool pool = new();
        string scratch = Path.Combine(_root, "spill-recycle");
        AssembledStream assembled = await AssembleStream(SpillingEngine(scratch, chunkByteBudget: 512, pool), profile);

        Assert.Equal(60, assembled.SourceFiles.Count);
        Assert.Equal(60, pool.RentedEvaluations);                 // the spill path actually ran
        Assert.Equal(pool.RentedEvaluations, pool.ReturnedEvaluations);   // borrowed == returned
        // Retained set is bounded by a chunk's carriers (a handful), NOT by the file count — the
        // memory-flatness property. 60 files, retained must stay tiny because each chunk recycles
        // before the next is built.
        Assert.True(pool.RetainedCount < 30,
            $"pool retained {pool.RetainedCount} carriers — expected a small chunk-bounded set, not file-count-proportional");
    }

    [Fact]
    public async Task Spilled_stream_with_many_tiny_chunks_has_no_cross_chunk_aliasing()
    {
        // Force spill AND many chunks (one file each). If a later chunk's carrier reuse corrupted an
        // earlier chunk, the assembled report would diverge from the unpooled baseline. AssembleStream
        // copies each chunk's data out on arrival (never retaining a view), as a buffering consumer of
        // pool-owned chunks must.
        for (int i = 0; i < 25; i++)
        {
            SourceFile($"{i:D3}.txt", $"content number {i}");
            if (i % 2 == 0) TargetFile($"{i:D3}.txt", $"content number {i}");   // SkipUnchanged: dest file + SubjectIndex
        }
        Profile profile = ProfileUnderTest();

        string scratch = Path.Combine(_root, "spill-alias");
        AssembledStream pooled = await AssembleStream(SpillingEngine(scratch, chunkByteBudget: 256, pool: null), profile);
        AssembledStream unpooled = await AssembleStream(InMemoryStreamEngine(chunkByteBudget: 256), profile);

        AssertSameContent(unpooled, pooled);
        Assert.Equal(25, pooled.SourceFiles.Count);
    }

    [Fact]
    public async Task Stream_reports_a_spool_write_failure_as_a_failure_item()
    {
        for (int i = 0; i < 40; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        // A *file* at the scratch path makes the spill's Directory.CreateDirectory throw, faulting
        // the spool writer on the first record. The stream must end with a single failure item —
        // not hang with workers blocked on the dead spool channel, and not tear the enumerator with
        // a raw exception.
        string scratch = Path.Combine(_root, "scratch-is-a-file");
        File.WriteAllText(scratch, "not a directory");

        DryRunEngine engine = SpillingEngine(scratch, chunkByteBudget: 4000, pool: null);
        List<Result<DryRunChunk, string>> items = [];
        await Task.Run(async () =>
        {
            await foreach (Result<DryRunChunk, string> item in engine.SimulateStreamAsync(profile, null))
                items.Add(item);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Result<DryRunChunk, string> only = Assert.Single(items);
        Assert.True(only.TryGetError(out string? error));
        Assert.Contains("spool failed", error);
    }

    [Fact]
    public async Task Stream_cancellation_throws_from_the_enumerator()
    {
        SourceFile("a.txt");
        SourceFile("b.txt");
        Profile profile = ProfileUnderTest();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in NewEngine().SimulateStreamAsync(profile, null, ct: cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Read_side_spool_fault_is_a_single_failure_item_not_a_torn_stream()
    {
        // The write side already converts spool faults to a failure item; a corrupted snapshot
        // discovered on the REPLAY (read) side must surface the same way — never a raw IOException
        // tearing the streaming enumerator.
        SourceFile("a.txt");
        Profile profile = ProfileUnderTest();
        DryRunEngine engine = BuildEngine(GlobalSettings.Default, chunkByteBudget: 1 << 20,
            spillThresholdBytes: DryRunEngine.WireChunkByteBudget,
            spoolFactory: new ReadFaultingSpoolFactory(), pool: null);

        List<Result<DryRunChunk, string>> items = [];
        await foreach (Result<DryRunChunk, string> item in engine.SimulateStreamAsync(profile, null))
            items.Add(item);

        Result<DryRunChunk, string> only = Assert.Single(items);
        Assert.True(only.TryGetError(out string? error));
        Assert.Contains("spool failed", error);
        Assert.Contains("truncated length prefix", error);
    }

    private sealed class ReadFaultingSpoolFactory : IDryRunSpoolFactory
    {
        public IDryRunSpool Create(EvaluationCarrierPool pool) => new ReadFaultingSpool();
    }

    /// <summary>Accepts writes normally, then fails the replay the way a corrupted on-disk snapshot
    /// does (<see cref="FileDryRunSpool"/> throws IOException on a truncated/implausible record).</summary>
    private sealed class ReadFaultingSpool : IDryRunSpool
    {
        private readonly bool _corrupt = true;

        public ValueTask WriteAsync(FileEvaluation evaluation, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask CompleteWritingAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<IEvaluationView> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (_corrupt)
                throw new IOException("the dry-run snapshot ended mid-record (truncated length prefix)");
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ----- spill/pooling harness -----

    /// <summary>An engine whose streamed path spills to <paramref name="scratch"/> immediately
    /// (SpillThresholdBytes = 1), so the read-back exercises the pooled-carrier path. A null
    /// <paramref name="pool"/> lets the engine build its own; a supplied one is inspectable afterward.</summary>
    private DryRunEngine SpillingEngine(string scratch, int chunkByteBudget, EvaluationCarrierPool? pool) =>
        BuildEngine(GlobalSettings.Default with { ScratchDirectory = scratch },
            chunkByteBudget, spillThresholdBytes: 1, spoolFactory: null, pool);

    /// <summary>An engine whose streamed path uses the in-memory spool (no serialization, no pooling) —
    /// the unpooled baseline the spilled run must match.</summary>
    private DryRunEngine InMemoryStreamEngine(int chunkByteBudget) =>
        BuildEngine(GlobalSettings.Default, chunkByteBudget, spillThresholdBytes: DryRunEngine.WireChunkByteBudget,
            spoolFactory: new InMemoryDryRunSpoolFactory(), pool: null);

    private static DryRunEngine BuildEngine(
        GlobalSettings global, int chunkByteBudget, long spillThresholdBytes,
        IDryRunSpoolFactory? spoolFactory, EvaluationCarrierPool? pool)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        FakeSettings settings = new(global);
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fileSystem, settings);
        return new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new SourceScanner(TimeProvider.System, scheduler),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            settings,
            TimeProvider.System,
            new DestinationProjector(NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler))
        {
            ChunkByteBudget = chunkByteBudget,
            SpillThresholdBytes = spillThresholdBytes,
            SpoolFactory = spoolFactory,
            StreamCarrierPool = pool,
        };
    }

    /// <summary>A run's streamed chunks flattened into global lists, copied out of each chunk on arrival
    /// (never retaining a pool-owned view — the buffering-consumer contract).</summary>
    private sealed record AssembledStream(
        List<(string Path, string Root, long Length)> SourceFiles,
        List<(string Path, string Root, long Length)> DestFiles,
        Dictionary<int, (string Path, OperationKind Kind, OnSuccessAction? Disp)> SourceOps,
        List<(int SourceIndex, int SubjectIndex, string Path, OperationKind Kind, string? Detail)> DestOps);

    private static async Task<AssembledStream> AssembleStream(DryRunEngine engine, Profile profile)
    {
        List<(string, string, long)> sourceFiles = [];
        List<(string, string, long)> destFiles = [];
        Dictionary<int, (string, OperationKind, OnSuccessAction?)> sourceOps = [];
        List<(int, int, string, OperationKind, string?)> destOps = [];
        await foreach (Result<DryRunChunk, string> item in engine.SimulateStreamAsync(profile, null))
        {
            Assert.True(item.TryGetValue(out DryRunChunk? c), "stream yielded a failure chunk");
            foreach (IPhysicalFileView f in c!.SourceFiles) sourceFiles.Add((f.Path, f.Root, f.Length));
            foreach (IPhysicalFileView f in c.DestinationFiles) destFiles.Add((f.Path, f.Root, f.Length));
            foreach (IFileOperationView o in c.SourceOperations) sourceOps[o.SourceIndex] = (o.Path, o.Kind, o.SourceDisposition);
            foreach (IFileOperationView o in c.DestinationOperations) destOps.Add((o.SourceIndex, o.SubjectIndex, o.Path, o.Kind, o.Detail));
        }
        return new AssembledStream(sourceFiles, destFiles, sourceOps, destOps);
    }

    /// <summary>Asserts two assembled streams carry the same report content. Compares each projected
    /// list separately so xUnit does a structural (element-wise) comparison — a tuple-of-lists compares
    /// by list reference and would spuriously pass/fail.</summary>
    private static void AssertSameContent(AssembledStream expected, AssembledStream actual)
    {
        var e = Normalize(expected);
        var a = Normalize(actual);
        Assert.Equal(e.Src, a.Src);
        Assert.Equal(e.Dst, a.Dst);
        Assert.Equal(e.SrcOps, a.SrcOps);
        Assert.Equal(e.DstOps, a.DstOps);
    }

    /// <summary>Order-independent projection: sorted file lists and ops with their integer indices
    /// resolved to the referenced file paths, so two runs whose discovery (index) order differs still
    /// compare equal when — and only when — their report content and the index remap agree.</summary>
    private static (List<string> Src, List<string> Dst, List<string> SrcOps, List<string> DstOps) Normalize(AssembledStream a)
    {
        List<string> src = a.SourceFiles.Select(f => $"{f.Path}|{f.Root}|{f.Length}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        List<string> dst = a.DestFiles.Select(f => $"{f.Path}|{f.Root}|{f.Length}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        List<string> srcOps = a.SourceOps.Values.Select(o => $"{o.Path}|{o.Kind}|{o.Disp}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        List<string> dstOps = a.DestOps.Select(o =>
        {
            string sp = o.SourceIndex >= 0 ? a.SourceFiles[o.SourceIndex].Path : "-";
            string bp = o.SubjectIndex >= 0 ? a.DestFiles[o.SubjectIndex].Path : "-";
            return $"{o.Path}|{o.Kind}|src:{sp}|subj:{bp}|{o.Detail}";
        }).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return (src, dst, srcOps, dstOps);
    }

    private Dictionary<string, (long Length, DateTime Mtime)> SnapshotTree() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
}
