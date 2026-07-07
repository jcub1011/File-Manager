using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
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

    private static DryRunEngine NewEngine(params Profile[] profiles)
    {
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        return new DryRunEngine(
            NullLogger<DryRunEngine>.Instance,
            new FakeCatalog(profiles),
            new SourceScanner(NullLogger<SourceScanner>.Instance, fileSystem, TimeProvider.System),
            new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System),
            new FileHasher(NullLogger<FileHasher>.Instance),
            new ConflictResolver(NullLogger<ConflictResolver>.Instance),
            TimeProvider.System);
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
        DryRunTargetAction action = Assert.Single(file.Targets);
        Assert.Equal(DryRunTargetKind.WouldWrite, action.Kind);
        Assert.Equal(Path.Combine(_target, "new.txt"), action.TargetPath);
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

    private Dictionary<string, (long Length, DateTime Mtime)> SnapshotTree() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
}
