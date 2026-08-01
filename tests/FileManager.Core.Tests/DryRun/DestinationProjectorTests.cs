using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
using FileManager.Core.Files;
using FileManager.Core.Scanning;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.DryRun;

/// <summary>The read-only destination sweep that fills the Destinations view's gaps: pre-existing
/// Untouched files and Mirror orphans. Every P0/P1 rule from the design review gets a case here.</summary>
public sealed class DestinationProjectorTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _target;

    public DestinationProjectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-destproj-" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A projector wired to a scan scheduler over the real file system. maxThreads pins the global and
    // per-drive scan budgets (1 = serial); null lets them auto-scale. Local temp dirs → not network.
    private static DestinationProjector NewProjector(int? maxThreads = null)
    {
        FileSystemService fs = new(NullLogger<FileSystemService>.Instance);
        GlobalSettings settings = maxThreads is int n
            ? new GlobalSettings
            {
                ScanThreading = new ScanThreadingSettings
                {
                    MaxScanThreads = ThreadBudget.Explicit(n),
                    PerDriveDefault = ThreadBudget.Explicit(n),
                },
            }
            : GlobalSettings.Default;
        ScanScheduler scheduler = new(NullLogger<ScanScheduler>.Instance, fs, new FakeSettingsProvider(settings));
        return new DestinationProjector(NullLogger<DestinationProjector>.Instance, new FakeVolumeInfoProvider(), scheduler);
    }

    private Profile Mirror() => TestProfiles.Valid(_source, _target) with { SyncMode = SyncMode.Mirror };
    private Profile Additive() => TestProfiles.Valid(_source, _target);

    private string TargetFile(string relative, string content = "x")
    {
        string full = Path.Combine(_target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    // A destination operation naming a resulting path — the sweep treats every op's Path as a
    // survivor, so its Kind is irrelevant to the survivor logic.
    private static VirtualFileOperation WriteTo(string targetPath, OperationKind kind = OperationKind.New) => new()
    {
        Path = targetPath,
        Root = @"C:\ignored",
        Kind = kind,
        SourceIndex = 0,
    };

    // A pinned worker count > 1 so the sweep exercises its concurrent work-stealing walk in every case.
    private const int Workers = 4;

    private DestinationSweepResult Project(Profile profile, params VirtualFileOperation[] destinationOps) =>
        NewProjector(Workers).Project(profile, destinationOps, truncated: false, CancellationToken.None);

    [Fact]
    public void Mirror_orphan_is_deleted()
    {
        string orphan = TargetFile("orphan.txt");

        DestinationSweepResult result = Project(Mirror());
        VirtualFileOperation op = Assert.Single(result.Ops);
        Assert.Equal(orphan, op.Path);
        Assert.Equal(OperationKind.Deleted, op.Kind);
        // The op references its own swept physical file.
        Assert.Equal(orphan, result.Files[op.SubjectIndex].Path);
    }

    [Fact]
    public void Additive_preexisting_file_is_untouched_not_deleted()
    {
        TargetFile("keep.txt");

        VirtualFileOperation op = Assert.Single(Project(Additive()).Ops);
        Assert.Equal(OperationKind.Untouched, op.Kind);
    }

    [Fact]
    public void A_written_target_is_a_survivor_and_never_an_orphan()
    {
        string keep = TargetFile("keep.txt");

        Assert.Empty(Project(Mirror(), WriteTo(keep)).Ops);
    }

    [Fact]
    public void Rename_keeps_both_the_original_and_the_suffixed_path_out_of_orphans()
    {
        string original = TargetFile("dup.txt");
        string suffixed = TargetFile("dup (1).txt");   // the renamed-to output would land here

        // A rename emits two destination ops — Rename at the suffixed path and Untouched at the kept
        // original — so both paths are survivors and neither is swept as an orphan.
        Assert.Empty(Project(Mirror(),
            WriteTo(suffixed, OperationKind.Rename),
            WriteTo(original, OperationKind.Untouched)).Ops);
    }

    [Fact]
    public void A_file_under_a_source_root_is_excluded_even_when_the_target_contains_the_source()
    {
        // Target contains the source (validator only warns on this overlap).
        string nestedSource = Path.Combine(_target, "sub");
        Directory.CreateDirectory(nestedSource);
        Profile profile = Mirror() with { Sources = [new SourceConfig { Path = nestedSource }] };

        File.WriteAllText(Path.Combine(nestedSource, "data.txt"), "x");   // a source file living under the target
        string realOrphan = TargetFile("other.txt");

        VirtualFileOperation op = Assert.Single(Project(profile).Ops);
        Assert.Equal(realOrphan, op.Path);   // the source file is not previewed as a deletion
    }

    [Fact]
    public void Infrastructure_directories_and_temp_files_are_ignored()
    {
        TargetFile(Path.Combine(".pipeline_tmp", "x.txt"));
        TargetFile(Path.Combine(".fm_staging", "y.txt"));
        TargetFile("foo.fmtmp-123");
        string real = TargetFile("real.txt");

        VirtualFileOperation op = Assert.Single(Project(Mirror()).Ops);
        Assert.Equal(real, op.Path);
    }

    [Fact]
    public void Deep_orphans_are_deleted_regardless_of_the_source_max_depth()
    {
        string deep = TargetFile(Path.Combine("a", "b", "c", "deep.txt"));
        Profile profile = Mirror() with { Filters = new FilterSet { MaxDepth = 1 } };   // a source-side limit

        VirtualFileOperation op = Assert.Single(Project(profile).Ops);
        Assert.Equal(deep, op.Path);
        Assert.Equal(OperationKind.Deleted, op.Kind);
    }

    [Fact]
    public void A_truncated_pass_emits_no_entries()
    {
        TargetFile("orphan.txt");

        Assert.Empty(NewProjector(Workers).Project(Mirror(), [], truncated: true, CancellationToken.None).Ops);
    }

    [Fact]
    public void Survivor_matching_is_case_insensitive_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string onDisk = TargetFile("Keep.txt");
        string writtenAs = Path.Combine(_target, "keep.txt");   // different case

        Assert.Empty(Project(Mirror(), WriteTo(writtenAs)).Ops);
        Assert.NotEqual(onDisk, writtenAs);   // sanity: the strings really differ in case
    }

    [Fact]
    public void Missing_target_root_yields_no_entries_and_does_not_throw()
    {
        Profile profile = Mirror() with { Targets = [new TargetConfig { Path = Path.Combine(_root, "does-not-exist") }] };
        Assert.Empty(Project(profile).Ops);
    }

    [Fact]
    public void Every_orphan_is_found_with_a_valid_index_pairing_regardless_of_tree_shape()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 20; i++)                                                  // wide: many shallow subtrees
            expected.Add(TargetFile(Path.Combine($"w{i}", $"f{i}.txt")));
        expected.Add(TargetFile(Path.Combine("a", "b", "c", "d", "e", "deep.txt")));  // deep/narrow chain

        DestinationSweepResult result = Project(Mirror());

        Assert.Equal(expected.Count, result.Files.Count);
        Assert.Equal(result.Files.Count, result.Ops.Count);
        for (int i = 0; i < result.Ops.Count; i++)
        {
            Assert.Equal(i, result.Ops[i].SubjectIndex);              // Ops[i] references Files[i]
            Assert.Equal(result.Files[i].Path, result.Ops[i].Path);
            Assert.Equal(OperationKind.Deleted, result.Ops[i].Kind);
        }
        Assert.Equal(expected, result.Ops.Select(o => o.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Result_is_identical_across_worker_counts()
    {
        for (int i = 0; i < 25; i++)
            TargetFile(Path.Combine($"d{i % 5}", $"f{i}.txt"));

        DestinationSweepResult one = NewProjector(1).Project(Mirror(), [], truncated: false, CancellationToken.None);
        DestinationSweepResult many = NewProjector(8).Project(Mirror(), [], truncated: false, CancellationToken.None);
        DestinationSweepResult auto = NewProjector(null).Project(Mirror(), [], truncated: false, CancellationToken.None);

        // The sorted merge makes the output order-stable regardless of how the concurrent walk raced
        // or how many threads the auto (medium-aware) degree of parallelism chose.
        var expected = one.Ops.Select(o => (o.Path, o.Kind)).ToList();
        Assert.Equal(expected, many.Ops.Select(o => (o.Path, o.Kind)));
        Assert.Equal(expected, auto.Ops.Select(o => (o.Path, o.Kind)));
        Assert.Equal(one.Files.Select(f => f.Path), many.Files.Select(f => f.Path));
    }

    [Fact]
    public void Overlapping_target_roots_report_a_shared_file_once()
    {
        // Two target roots where one is nested under the other. The nested root is pruned before the
        // walk (the parent's walk already covers its subtree), so the file is enumerated once — rather
        // than enumerated twice and deduped afterwards, which is what this used to assert.
        string child = Path.Combine(_target, "shared");
        Directory.CreateDirectory(child);
        string overlap = Path.Combine(child, "both.txt");
        File.WriteAllText(overlap, "x");

        Profile profile = Mirror() with
        {
            Targets = [new TargetConfig { Path = _target }, new TargetConfig { Path = child }],
        };

        DestinationSweepResult result = Project(profile);

        Assert.Single(result.Ops, o => string.Equals(o.Path, overlap, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(result.Files.Count, result.Ops.Count);   // still index-paired
    }

    [Fact]
    public void Nested_target_roots_report_the_outermost_root_deterministically()
    {
        // Before nested-root pruning this was a latent bug: the file was enumerated under BOTH roots
        // and the merge kept whichever copy the sort left first — but List<T>.Sort is an unstable
        // introsort and the comparison was on Path alone, so the reported Root was arbitrary. Pruning
        // makes it always the outermost root, and this asserts the Root the old test never looked at.
        string child = Path.Combine(_target, "shared");
        Directory.CreateDirectory(child);
        for (int i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(child, $"f{i}.txt"), "x");

        // Declared child-first, so a naive "first root wins" would report the child.
        Profile profile = Mirror() with
        {
            Targets = [new TargetConfig { Path = child }, new TargetConfig { Path = _target }],
        };

        for (int attempt = 0; attempt < 5; attempt++)
        {
            DestinationSweepResult result = Project(profile);
            Assert.Equal(20, result.Files.Count);
            Assert.All(result.Files, f => Assert.Equal(_target, f.Root));
            Assert.All(result.Ops, o => Assert.Equal(_target, o.Root));
        }
    }

    // ── Streamed sweep (SweepStreamAsync) ────────────────────────────────────────────────────────
    // The GUI path. Emits byte-budgeted chunks in discovery order as the walk produces them, instead
    // of collecting and sorting every entry first, so its live set is one chunk rather than one
    // record pair per pre-existing file. Order is deliberately NOT part of its contract (the client
    // re-sorts by path-relative-to-root); the set, the classification, the global SubjectIndex
    // pairing and the reported Root are.

    private sealed record StreamedSweep(
        List<PhysicalFile> Files, List<VirtualFileOperation> Ops, bool Capped, List<int> ChunkSizes);

    private static async Task<StreamedSweep> SweepStream(
        DestinationProjector projector, Profile profile, int maxEntries = int.MaxValue,
        int indexBase = 0, int chunkByteBudget = DryRunEngine.WireChunkByteBudget, bool truncated = false)
    {
        List<PhysicalFile> files = [];
        List<VirtualFileOperation> ops = [];
        List<int> chunkSizes = [];
        bool capped = false;
        HashSet<NormalizedPath> survivors = [];
        await foreach (Result<DryRunChunk, string> result in projector.SweepStreamAsync(
            profile, survivors, truncated, maxEntries, indexBase, chunkByteBudget,
            progress: null, CancellationToken.None))
        {
            Assert.False(result.TryGetError(out string? error), error);
            result.TryGetValue(out DryRunChunk? chunk);
            capped |= chunk!.SweepCapped;
            // Chunks carry destination entries only — there is no source half in a sweep.
            Assert.Empty(chunk.SourceFiles);
            Assert.Empty(chunk.SourceOperations);
            Assert.Equal(chunk.DestinationFiles.Count, chunk.DestinationOperations.Count);
            if (chunk.DestinationFiles.Count > 0)
                chunkSizes.Add(chunk.DestinationFiles.Count);
            foreach (IPhysicalFileView f in chunk.DestinationFiles)
                files.Add((PhysicalFile)f);
            foreach (IFileOperationView o in chunk.DestinationOperations)
                ops.Add((VirtualFileOperation)o);
        }
        return new StreamedSweep(files, ops, capped, chunkSizes);
    }

    [Fact]
    public async Task Streamed_sweep_reports_the_same_set_and_classification_as_the_batched_sweep()
    {
        for (int i = 0; i < 40; i++)
            TargetFile(Path.Combine($"d{i % 6}", $"f{i}.txt"));
        TargetFile("top.txt");

        DestinationSweepResult batched = Project(Mirror());
        StreamedSweep streamed = await SweepStream(NewProjector(Workers), Mirror());

        // Same set, same classification — order is explicitly not compared.
        Assert.Equal(
            batched.Ops.Select(o => (o.Path, o.Kind, o.Root)).ToHashSet(),
            streamed.Ops.Select(o => (o.Path, o.Kind, o.Root)).ToHashSet());
        Assert.Equal(
            batched.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
            streamed.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.False(streamed.Capped);
    }

    [Fact]
    public async Task Streamed_sweep_keeps_subject_indices_global_across_chunk_boundaries()
    {
        for (int i = 0; i < 30; i++)
            TargetFile(Path.Combine($"d{i % 4}", $"f{i}.txt"));

        // A tiny budget forces many chunks, which is the case the index arithmetic can get wrong:
        // each op's SubjectIndex must address the CONCATENATED destination list, not its own chunk.
        const int indexBase = 17;
        StreamedSweep streamed = await SweepStream(
            NewProjector(Workers), Mirror(), indexBase: indexBase, chunkByteBudget: 800);

        Assert.True(streamed.ChunkSizes.Count > 1, "expected the small budget to split the sweep into several chunks");
        Assert.Equal(30, streamed.Files.Count);
        Assert.Equal(30, streamed.Ops.Count);
        for (int i = 0; i < streamed.Ops.Count; i++)
        {
            // Ops[i] references Files[i] once the base is subtracted, and every op is source-less.
            Assert.Equal(indexBase + i, streamed.Ops[i].SubjectIndex);
            Assert.Equal(streamed.Files[i].Path, streamed.Ops[i].Path);
            Assert.Equal(-1, streamed.Ops[i].SourceIndex);
        }
    }

    [Fact]
    public async Task Streamed_sweep_marks_capped_when_the_entry_budget_trips()
    {
        for (int i = 0; i < 50; i++)
            TargetFile(Path.Combine($"d{i % 5}", $"f{i}.txt"));

        const int budget = 10;
        StreamedSweep streamed = await SweepStream(NewProjector(Workers), Mirror(), maxEntries: budget);

        Assert.True(streamed.Capped);
        Assert.True(
            streamed.Files.Count <= budget,
            $"emitted {streamed.Files.Count} entries against a budget of {budget}");
        Assert.Equal(streamed.Files.Count, streamed.Ops.Count);   // still whole (file, op) pairs
    }

    [Fact]
    public async Task Streamed_sweep_emits_nothing_when_the_source_pass_truncated()
    {
        // Same soundness gate as the batched path: a prefix-only survivor set makes every "no source
        // writes here" judgement untrustworthy, so a Mirror preview must not fabricate deletions.
        for (int i = 0; i < 5; i++)
            TargetFile($"orphan{i}.txt");

        StreamedSweep streamed = await SweepStream(NewProjector(Workers), Mirror(), truncated: true);

        Assert.Empty(streamed.Files);
        Assert.Empty(streamed.Ops);
        Assert.False(streamed.Capped);
    }

    [Fact]
    public async Task Streamed_sweep_reports_the_outermost_root_for_nested_target_roots()
    {
        string child = Path.Combine(_target, "shared");
        Directory.CreateDirectory(child);
        string overlap = Path.Combine(child, "both.txt");
        File.WriteAllText(overlap, "x");

        Profile profile = Mirror() with
        {
            Targets = [new TargetConfig { Path = child }, new TargetConfig { Path = _target }],
        };

        StreamedSweep streamed = await SweepStream(NewProjector(Workers), profile);

        // Reported once (the nested root was pruned, so it was never enumerated twice) and under the
        // outermost root. Streaming cannot afford the merge's per-path dedup set, so pruning is what
        // makes this correct rather than a post-hoc fix-up.
        VirtualFileOperation op = Assert.Single(streamed.Ops);
        Assert.Equal(overlap, op.Path);
        Assert.Equal(_target, op.Root);
        Assert.Equal(_target, Assert.Single(streamed.Files).Root);
    }

    [Fact]
    public void The_entry_budget_is_respected_under_parallelism()
    {
        for (int i = 0; i < 50; i++)
            TargetFile(Path.Combine($"d{i % 5}", $"f{i}.txt"));

        const int budget = 10;
        DestinationSweepResult result = Sweep(Mirror(), maxEntries: budget);

        Assert.True(result.Files.Count <= budget);
        Assert.Equal(result.Files.Count, result.Ops.Count);
        Assert.True(result.Truncated);
    }

    private DestinationSweepResult Sweep(Profile profile, int maxEntries) =>
        NewProjector(8).Sweep(profile, new HashSet<NormalizedPath>(), truncated: false, CancellationToken.None, maxEntries);

    // The sweep's per-file hot path wraps each enumerated path with NormalizedPath.FromCanonical
    // (skipping Create's Path.GetFullPath) on the guarantee that a path enumerated beneath an
    // already-canonical root is itself canonical. This locks that guarantee in: for every entry the
    // walk would see — seeded exactly as the projector seeds it, from a Create-canonicalized root —
    // the trusted wrap must produce the identical value the full Create would. A divergence here would
    // silently break survivor matching (a source's own target re-reported as an orphan deletion).
    [Fact]
    public void FromCanonical_matches_Create_for_every_enumerated_path()
    {
        TargetFile(Path.Combine("a", "b", "c", "deep.txt"));
        TargetFile("top.txt");
        for (int i = 0; i < 10; i++)
            TargetFile(Path.Combine($"w{i}", $"f{i}.txt"));

        var fs = new FileSystemService(NullLogger<FileSystemService>.Instance);
        // Seed from the canonical target root, mirroring DestinationProjector.Sweep.
        Assert.True(NormalizedPath.Create(_target).TryGetValue(out NormalizedPath root));

        int checkedEntries = 0;
        void Recurse(string dir)
        {
            foreach (Result<FileSystemEntry, EnumerationFault> entry in fs.EnumerateEntries(dir))
            {
                Assert.True(entry.TryGetValue(out FileSystemEntry? e));
                Assert.True(NormalizedPath.Create(e!.FullPath).TryGetValue(out NormalizedPath viaCreate));
                NormalizedPath viaTrusted = NormalizedPath.FromCanonical(e.FullPath);
                Assert.Equal(viaCreate.Value, viaTrusted.Value);   // Ordinal: byte-for-byte identical
                Assert.Equal(viaCreate, viaTrusted);               // and equal under the path comparer
                checkedEntries++;
                if (e.IsDirectory)
                    Recurse(e.FullPath);
            }
        }
        Recurse(root.Value);

        Assert.True(checkedEntries >= 12);   // sanity: the tree was actually walked
    }
}
