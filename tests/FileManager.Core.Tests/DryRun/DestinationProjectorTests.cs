using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
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
        new(NullLogger<DestinationProjector>.Instance, new FileSystemService(NullLogger<FileSystemService>.Instance));

    private Profile Mirror() => TestProfiles.Valid(_source, _target) with { SyncMode = SyncMode.Mirror };
    private Profile Additive() => TestProfiles.Valid(_source, _target);

    private string TargetFile(string relative, string content = "x")
    {
        string full = Path.Combine(_target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static DryRunFileResult WriteTo(string targetPath, DryRunTargetKind kind = DryRunTargetKind.WouldWrite, string? detail = null) => new()
    {
        SourcePath = @"C:\ignored\f.txt",
        Disposition = DryRunFileDisposition.WouldProcess,
        Targets = [new DryRunTargetAction { TargetPath = targetPath, Kind = kind, Detail = detail }],
    };

    private IReadOnlyList<DryRunDestinationEntry> Project(Profile profile, params DryRunFileResult[] files) =>
        NewProjector().Project(profile, files, truncated: false, CancellationToken.None);

    [Fact]
    public void Mirror_orphan_is_deleted()
    {
        string orphan = TargetFile("orphan.txt");

        DryRunDestinationEntry entry = Assert.Single(Project(Mirror()));
        Assert.Equal(orphan, entry.TargetPath);
        Assert.Equal(DryRunDestinationDisposition.Deleted, entry.Disposition);
    }

    [Fact]
    public void Additive_preexisting_file_is_untouched_not_deleted()
    {
        TargetFile("keep.txt");

        DryRunDestinationEntry entry = Assert.Single(Project(Additive()));
        Assert.Equal(DryRunDestinationDisposition.Untouched, entry.Disposition);
    }

    [Fact]
    public void A_written_target_is_a_survivor_and_never_an_orphan()
    {
        string keep = TargetFile("keep.txt");

        Assert.Empty(Project(Mirror(), WriteTo(keep)));
    }

    [Fact]
    public void Rename_keeps_both_the_original_and_the_suffixed_path_out_of_orphans()
    {
        string original = TargetFile("dup.txt");
        string suffixed = TargetFile("dup (1).txt");   // the renamed-to output would land here

        // WouldRenameTo: original (the survivor that forced the suffix) + Detail (the new path).
        Assert.Empty(Project(Mirror(), WriteTo(original, DryRunTargetKind.WouldRenameTo, detail: suffixed)));
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

        DryRunDestinationEntry entry = Assert.Single(Project(profile));
        Assert.Equal(realOrphan, entry.TargetPath);   // the source file is not previewed as a deletion
    }

    [Fact]
    public void Infrastructure_directories_and_temp_files_are_ignored()
    {
        TargetFile(Path.Combine(".pipeline_tmp", "x.txt"));
        TargetFile(Path.Combine(".fm_staging", "y.txt"));
        TargetFile("foo.fmtmp-123");
        string real = TargetFile("real.txt");

        DryRunDestinationEntry entry = Assert.Single(Project(Mirror()));
        Assert.Equal(real, entry.TargetPath);
    }

    [Fact]
    public void Deep_orphans_are_deleted_regardless_of_the_source_max_depth()
    {
        string deep = TargetFile(Path.Combine("a", "b", "c", "deep.txt"));
        Profile profile = Mirror() with { Filters = new FilterSet { MaxDepth = 1 } };   // a source-side limit

        DryRunDestinationEntry entry = Assert.Single(Project(profile));
        Assert.Equal(deep, entry.TargetPath);
        Assert.Equal(DryRunDestinationDisposition.Deleted, entry.Disposition);
    }

    [Fact]
    public void A_truncated_pass_emits_no_entries()
    {
        TargetFile("orphan.txt");

        Assert.Empty(NewProjector().Project(Mirror(), [], truncated: true, CancellationToken.None));
    }

    [Fact]
    public void Survivor_matching_is_case_insensitive_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string onDisk = TargetFile("Keep.txt");
        string writtenAs = Path.Combine(_target, "keep.txt");   // different case

        Assert.Empty(Project(Mirror(), WriteTo(writtenAs)));
        Assert.NotEqual(onDisk, writtenAs);   // sanity: the strings really differ in case
    }

    [Fact]
    public void Missing_target_root_yields_no_entries_and_does_not_throw()
    {
        Profile profile = Mirror() with { Targets = [new TargetConfig { Path = Path.Combine(_root, "does-not-exist") }] };
        Assert.Empty(Project(profile));
    }
}
