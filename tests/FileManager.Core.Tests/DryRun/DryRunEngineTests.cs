using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Contracts.Settings;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
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

    private sealed class FakeCatalog(params Profile[] profiles) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = profiles;
        public IReadOnlyList<Profile> Active => All.Where(p => p.Active).ToList();
        public IDisposable Subscribe(Action changeHandler) => throw new NotSupportedException();
        public Result Reload() => Result.Success();
    }

    // ----- concurrency resolution -----

    private static readonly int ExpectedAutoWorkers = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));

    private Profile ProfileWithConcurrency(ConcurrencyMode mode, int? manualWorkers = null) =>
        ProfileUnderTest() with { Concurrency = new ConcurrencyOverride { Mode = mode, ManualWorkers = manualWorkers } };

    [Fact]
    public void ResolveWorkers_profile_manual_overrides_the_global_setting()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes,
            GlobalSettings.Default with { DryRunConcurrencyMode = ConcurrencyMode.Manual, DryRunManualWorkers = 3 });
        Assert.Equal(5, engine.ResolveWorkers(ProfileWithConcurrency(ConcurrencyMode.Manual, 5)));
    }

    [Fact]
    public void ResolveWorkers_profile_automatic_uses_the_auto_formula()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes,
            GlobalSettings.Default with { DryRunConcurrencyMode = ConcurrencyMode.Manual, DryRunManualWorkers = 3 });
        Assert.Equal(ExpectedAutoWorkers, engine.ResolveWorkers(ProfileWithConcurrency(ConcurrencyMode.Automatic)));
    }

    [Fact]
    public void ResolveWorkers_inherit_uses_the_global_manual_value()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes,
            GlobalSettings.Default with { DryRunConcurrencyMode = ConcurrencyMode.Manual, DryRunManualWorkers = 4 });
        Assert.Equal(4, engine.ResolveWorkers(ProfileWithConcurrency(ConcurrencyMode.Inherit)));
    }

    [Fact]
    public void ResolveWorkers_inherit_uses_the_auto_formula_when_global_is_automatic()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, GlobalSettings.Default);   // global = Automatic
        Assert.Equal(ExpectedAutoWorkers, engine.ResolveWorkers(ProfileWithConcurrency(ConcurrencyMode.Inherit)));
    }

    [Fact]
    public void ResolveWorkers_clamps_a_manual_value_below_one()
    {
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, GlobalSettings.Default);
        Assert.Equal(1, engine.ResolveWorkers(ProfileWithConcurrency(ConcurrencyMode.Manual, 0)));
    }

    private static DryRunEngine NewEngine(params Profile[] profiles) =>
        NewEngine(DryRunEngine.MaxReportBytes, GlobalSettings.Default, profiles);

    private static DryRunEngine NewEngine(int reportByteBudget, params Profile[] profiles) =>
        NewEngine(reportByteBudget, GlobalSettings.Default, profiles);

    private static DryRunEngine NewEngine(int reportByteBudget, GlobalSettings global, params Profile[] profiles) =>
        NewEngine(reportByteBudget, DryRunEngine.ChunkByteThreshold, global, profiles);

    private static DryRunEngine NewEngine(int reportByteBudget, int chunkByteBudget, GlobalSettings global, params Profile[] profiles) =>
        NewEngine(reportByteBudget, chunkByteBudget, DryRunEngine.MaxStreamedFiles, global, profiles);

    private static DryRunEngine NewEngine(
        int reportByteBudget, int chunkByteBudget, int maxScannedCandidates, GlobalSettings global, params Profile[] profiles)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        return new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new FakeCatalog(profiles),
            new SourceScanner(NullLogger<SourceScanner>.Instance, fileSystem, TimeProvider.System),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(new(), new(), NullLogger<ConflictResolver>.Instance),
            new FakeSettings(global),
            TimeProvider.System)
        { ReportByteBudget = reportByteBudget, ChunkByteBudget = chunkByteBudget, MaxScannedCandidates = maxScannedCandidates };
    }

    private static async Task<List<DryRunFileResult>> CollectStream(
        DryRunEngine engine, Guid profileId, string? scope = null, CancellationToken ct = default)
    {
        List<DryRunFileResult> files = [];
        await foreach (Result<IReadOnlyList<DryRunFileResult>, string> chunk in engine.SimulateStreamAsync(profileId, scope, ct))
        {
            Assert.True(chunk.TryGetValue(out IReadOnlyList<DryRunFileResult>? batch), "stream yielded a failure chunk");
            files.AddRange(batch!);
        }
        return files;
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
        var simulated = await NewEngine(profile).SimulateAsync(profile.Id, scope);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));
        return report;
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

        DryRunFileResult file = Assert.Single(report.Files);
        Assert.Equal(DryRunFileDisposition.WouldProcess, file.Disposition);
        Assert.EndsWith("source", file.SourceRoot!, StringComparison.OrdinalIgnoreCase);
        DryRunTargetAction action = Assert.Single(file.Targets);
        Assert.Equal(DryRunTargetKind.WouldWrite, action.Kind);
        Assert.Equal(Path.Combine(_target, "new.txt"), action.TargetPath);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task Each_result_is_stamped_with_its_originating_source_root()
    {
        // Two sources, one file each: every result must carry the root it was enumerated under so the
        // GUI can group/filter a multi-source report by source.
        string sourceB = Path.Combine(_root, "source-b");
        Directory.CreateDirectory(sourceB);
        File.WriteAllText(Path.Combine(_source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(sourceB, "b.txt"), "b");

        Profile profile = ProfileUnderTest() with
        {
            Sources = [new SourceConfig { Path = _source }, new SourceConfig { Path = sourceB }],
        };

        DryRunReport report = await Simulate(profile);

        DryRunFileResult a = Assert.Single(report.Files, f => f.SourcePath.EndsWith("a.txt"));
        DryRunFileResult b = Assert.Single(report.Files, f => f.SourcePath.EndsWith("b.txt"));
        Assert.EndsWith("source", a.SourceRoot!, StringComparison.OrdinalIgnoreCase);      // ...\source
        Assert.EndsWith("source-b", b.SourceRoot!, StringComparison.OrdinalIgnoreCase);    // ...\source-b
        Assert.NotEqual(a.SourceRoot, b.SourceRoot);
    }

    [Fact]
    public async Task Report_truncates_at_the_byte_budget_and_the_serialized_response_fits()
    {
        const int budget = 2048;
        for (int i = 0; i < 20; i++)
            // Backslash-heavy nesting and a non-ASCII name: JSON escaping (`\` doubles,
            // non-ASCII becomes 6-byte \uXXXX) must count against the budget.
            SourceFile(Path.Combine("nested", "further", $"a-quite-long-file-name résumé ✓ {i:D4}.txt"), $"content {i}");
        Profile profile = ProfileUnderTest();

        var simulated = await NewEngine(budget, profile).SimulateAsync(profile.Id, null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));

        Assert.True(report!.Truncated);
        Assert.InRange(report.Files.Count, 1, 19);

        // The measurement is a true upper bound: the full wire response is the measured file
        // results plus commas and a bounded envelope.
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

        List<string> paths = report.Files.Select(f => f.SourcePath).ToList();
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

        var simulated = await NewEngine(1500, profile).SimulateAsync(profile.Id, null);
        Assert.True(simulated.TryGetValue(out DryRunReport? report));

        Assert.True(report!.Truncated);
        Assert.InRange(report.Files.Count, 1, 39);

        List<string> kept = report.Files.Select(f => Path.GetFileName(f.SourcePath)).ToList();
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

        DryRunFileResult skipped = Assert.Single(report.Files, f => f.SourcePath.EndsWith("notes.txt"));
        Assert.Equal(DryRunFileDisposition.WouldSkipFilter, skipped.Disposition);
        Assert.Contains("*.wav", skipped.DecidingFilter);

        DryRunFileResult matched = Assert.Single(report.Files, f => f.SourcePath.EndsWith("song.wav"));
        Assert.Equal(DryRunFileDisposition.WouldProcess, matched.Disposition);
    }

    [Fact]
    public async Task Identical_existing_target_is_an_unchanged_skip_and_suppresses_disposition()
    {
        SourceFile("same.txt", "identical bytes");
        TargetFile("same.txt", "identical bytes");
        DryRunReport report = await Simulate(ProfileUnderTest(onSuccess: OnSuccessAction.MoveToTrash));

        DryRunFileResult file = Assert.Single(report.Files);
        Assert.Equal(DryRunFileDisposition.WouldSkipUnchanged, file.Disposition);
        Assert.Equal(DryRunTargetKind.WouldSkipUnchanged, Assert.Single(file.Targets).Kind);
        // §3.4.1: an all-unchanged job closes Skipped without committing — no disposition runs.
        Assert.Null(file.SourceDisposition);
    }

    [Fact]
    public async Task Same_size_different_content_is_not_unchanged()
    {
        SourceFile("clash.txt", "AAAA");
        TargetFile("clash.txt", "BBBB");   // same length, different bytes
        DryRunReport report = await Simulate(ProfileUnderTest(conflict: ConflictResolution.Overwrite));

        DryRunTargetAction action = Assert.Single(Assert.Single(report.Files).Targets);
        Assert.Equal(DryRunTargetKind.WouldOverwrite, action.Kind);
    }

    [Fact]
    public async Task RenameSuffix_predicts_the_suffixed_name()
    {
        SourceFile("dup.txt", "new content");
        TargetFile("dup.txt", "old");
        DryRunReport report = await Simulate(ProfileUnderTest(conflict: ConflictResolution.RenameSuffix));

        DryRunTargetAction action = Assert.Single(Assert.Single(report.Files).Targets);
        Assert.Equal(DryRunTargetKind.WouldRenameTo, action.Kind);
        Assert.Equal(Path.Combine(_target, "dup (1).txt"), action.Detail);
    }

    [Fact]
    public async Task Skip_policy_reports_a_conflict_skip_but_still_processes()
    {
        SourceFile("kept.txt", "new content longer");
        TargetFile("kept.txt", "old");
        DryRunReport report = await Simulate(ProfileUnderTest(conflict: ConflictResolution.Skip,
            onSuccess: OnSuccessAction.MoveToTrash));

        DryRunFileResult file = Assert.Single(report.Files);
        Assert.Equal(DryRunTargetKind.WouldSkipConflict, Assert.Single(file.Targets).Kind);
        Assert.Equal(DryRunFileDisposition.WouldProcess, file.Disposition);
        Assert.Equal(nameof(OnSuccessAction.MoveToTrash), file.SourceDisposition);
    }

    [Fact]
    public async Task OverwriteIfNewer_goes_both_ways()
    {
        SourceFile("timed.txt", "newer source content!");
        TargetFile("timed.txt", "old");
        // Make the existing target NEWER than the source → skip; then OLDER → overwrite.
        File.SetLastWriteTimeUtc(Path.Combine(_target, "timed.txt"), DateTime.UtcNow.AddHours(1));
        DryRunReport newerTarget = await Simulate(ProfileUnderTest(conflict: ConflictResolution.OverwriteIfNewer));
        Assert.Equal(DryRunTargetKind.WouldSkipConflict, Assert.Single(Assert.Single(newerTarget.Files).Targets).Kind);

        File.SetLastWriteTimeUtc(Path.Combine(_target, "timed.txt"), DateTime.UtcNow.AddHours(-1));
        DryRunReport olderTarget = await Simulate(ProfileUnderTest(conflict: ConflictResolution.OverwriteIfNewer));
        Assert.Equal(DryRunTargetKind.WouldOverwrite, Assert.Single(Assert.Single(olderTarget.Files).Targets).Kind);
    }

    [Fact]
    public async Task PreserveStructure_keeps_relative_paths()
    {
        SourceFile(Path.Combine("nested", "deep", "file.txt"));
        DryRunReport report = await Simulate(ProfileUnderTest());

        DryRunTargetAction action = Assert.Single(Assert.Single(report.Files).Targets);
        Assert.Equal(Path.Combine(_target, "nested", "deep", "file.txt"), action.TargetPath);
    }

    [Fact]
    public async Task Unknown_profile_fails()
    {
        var simulated = await NewEngine(ProfileUnderTest()).SimulateAsync(Guid.NewGuid(), null);
        Assert.True(simulated.TryGetError(out string? error));
        Assert.Contains("not found", error);
    }

    [Fact]
    public async Task Cancellation_yields_a_Canceled_result_not_an_exception()
    {
        SourceFile("a.txt");
        SourceFile("b.txt");
        Profile profile = ProfileUnderTest();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        var simulated = await NewEngine(profile).SimulateAsync(profile.Id, null, cts.Token);

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
            DryRunEngine.MaxReportBytes, DryRunEngine.ChunkByteThreshold, maxScannedCandidates: 5,
            GlobalSettings.Default, profile);
        List<DryRunFileResult> streamed = await CollectStream(engine, profile.Id);

        Assert.Equal(5, streamed.Count);
    }

    [Fact]
    public async Task Stream_yields_the_same_results_in_the_same_order_as_the_batched_report()
    {
        string[] names = ["m.txt", "a.txt", "z.txt", "c.txt", "b.txt", "y.txt", "d.txt", "n.txt"];
        foreach (string name in names)
            SourceFile(name, $"content of {name}");
        Profile profile = ProfileUnderTest();

        DryRunReport batched = await Simulate(profile);
        List<DryRunFileResult> streamed = await CollectStream(NewEngine(profile), profile.Id);

        Assert.Equal(
            batched.Files.Select(f => (f.SourcePath, f.Disposition)).ToList(),
            streamed.Select(f => (f.SourcePath, f.Disposition)).ToList());
    }

    [Fact]
    public async Task Stream_has_no_truncation_ceiling_where_the_batched_report_truncates()
    {
        // A budget that truncates the single-frame report must NOT truncate the stream: streaming
        // reports every file across many frames.
        for (int i = 0; i < 40; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        var batched = await NewEngine(1500, profile).SimulateAsync(profile.Id, null);
        Assert.True(batched.TryGetValue(out DryRunReport? truncatedReport));
        Assert.True(truncatedReport!.Truncated);
        Assert.InRange(truncatedReport.Files.Count, 1, 39);

        // Same tiny budget as the *chunk* budget — still every file comes back.
        List<DryRunFileResult> streamed = await CollectStream(NewEngine(1500, 1500, GlobalSettings.Default, profile), profile.Id);
        Assert.Equal(40, streamed.Count);
        Assert.Equal(
            Enumerable.Range(0, 40).Select(i => $"{i:D3}.txt").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            streamed.Select(f => Path.GetFileName(f.SourcePath)).ToList());
    }

    [Fact]
    public async Task Stream_splits_into_multiple_chunks_at_the_chunk_budget()
    {
        for (int i = 0; i < 30; i++)
            SourceFile($"{i:D3}.txt", $"content {i}");
        Profile profile = ProfileUnderTest();

        // A small chunk budget forces several chunks; each stays under the 16 MiB frame cap by design.
        DryRunEngine engine = NewEngine(DryRunEngine.MaxReportBytes, 512, GlobalSettings.Default, profile);
        List<IReadOnlyList<DryRunFileResult>> chunks = [];
        await foreach (Result<IReadOnlyList<DryRunFileResult>, string> chunk in engine.SimulateStreamAsync(profile.Id, null))
        {
            Assert.True(chunk.TryGetValue(out IReadOnlyList<DryRunFileResult>? batch));
            chunks.Add(batch!);
        }

        Assert.True(chunks.Count > 1, $"expected multiple chunks, got {chunks.Count}");
        Assert.All(chunks, c => Assert.NotEmpty(c));
        Assert.Equal(30, chunks.Sum(c => c.Count));
    }

    [Fact]
    public async Task Stream_unknown_profile_yields_a_single_failure_item()
    {
        List<Result<IReadOnlyList<DryRunFileResult>, string>> items = [];
        await foreach (var item in NewEngine(ProfileUnderTest()).SimulateStreamAsync(Guid.NewGuid(), null))
            items.Add(item);

        Result<IReadOnlyList<DryRunFileResult>, string> only = Assert.Single(items);
        Assert.True(only.TryGetError(out string? error));
        Assert.Contains("not found", error);
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
            await foreach (var _ in NewEngine(profile).SimulateStreamAsync(profile.Id, null, cts.Token))
            {
            }
        });
    }

    private Dictionary<string, (long Length, DateTime Mtime)> SnapshotTree() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
}
