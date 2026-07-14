using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
using FileManager.Core.Files;
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

    private static DestinationProjector NewProjector() =>
        new(NullLogger<DestinationProjector>.Instance,
            new FileSystemService(NullLogger<FileSystemService>.Instance),
            new FakeVolumeInfoProvider());   // local temp dirs → not a network path

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
        NewProjector().Project(profile, destinationOps, truncated: false, manualWorkers: Workers, CancellationToken.None);

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

        Assert.Empty(NewProjector().Project(Mirror(), [], truncated: true, manualWorkers: Workers, CancellationToken.None).Ops);
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

        DestinationProjector projector = NewProjector();
        DestinationSweepResult one = projector.Project(Mirror(), [], truncated: false, manualWorkers: 1, CancellationToken.None);
        DestinationSweepResult many = projector.Project(Mirror(), [], truncated: false, manualWorkers: 8, CancellationToken.None);
        DestinationSweepResult auto = projector.Project(Mirror(), [], truncated: false, manualWorkers: null, CancellationToken.None);

        // The sorted merge makes the output order-stable regardless of how the concurrent walk raced
        // or how many threads the auto (medium-aware) degree of parallelism chose.
        var expected = one.Ops.Select(o => (o.Path, o.Kind)).ToList();
        Assert.Equal(expected, many.Ops.Select(o => (o.Path, o.Kind)));
        Assert.Equal(expected, auto.Ops.Select(o => (o.Path, o.Kind)));
        Assert.Equal(one.Files.Select(f => f.Path), many.Files.Select(f => f.Path));
    }

    [Fact]
    public void The_entry_budget_is_respected_under_parallelism()
    {
        for (int i = 0; i < 50; i++)
            TargetFile(Path.Combine($"d{i % 5}", $"f{i}.txt"));

        const int budget = 10;
        DestinationSweepResult result = Sweep(Mirror(), manualWorkers: 8, maxEntries: budget);

        Assert.True(result.Files.Count <= budget);
        Assert.Equal(result.Files.Count, result.Ops.Count);
        Assert.True(result.Truncated);
    }

    private DestinationSweepResult Sweep(Profile profile, int manualWorkers, int maxEntries) =>
        NewProjector().Sweep(profile, new HashSet<NormalizedPath>(), truncated: false, manualWorkers, CancellationToken.None, maxEntries);
}
