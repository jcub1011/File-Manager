using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Watching;

public sealed class SourceScannerTests : IDisposable
{
    private readonly string _root;

    public SourceScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SourceScanner NewScanner() => new(
        NullLogger<SourceScanner>.Instance,
        new FileSystemService(NullLogger<FileSystemService>.Instance),
        TimeProvider.System);

    private Profile ProfileOver(string sourcePath, FilterSet? filters = null) =>
        TestProfiles.Valid(sourcePath: sourcePath, targetPath: Path.Combine(_root, "unused-target"))
            with { Filters = filters };

    private string Touch(params string[] segments)
    {
        string path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static (List<Payload> Payloads, List<EnumerationFault> Faults) Collect(
        IEnumerable<FileManager.Contracts.Primitives.Result<Payload, EnumerationFault>> results)
    {
        List<Payload> payloads = [];
        List<EnumerationFault> faults = [];
        foreach (var result in results)
        {
            if (result.TryGetValue(out Payload? payload))
                payloads.Add(payload);
            else if (result.TryGetError(out EnumerationFault fault))
                faults.Add(fault);
        }
        return (payloads, faults);
    }

    [Fact]
    public void Recurses_and_stamps_source_root()
    {
        Touch("a.txt");
        string nested = Touch("sub", "deeper", "b.txt");

        var (payloads, faults) = Collect(NewScanner().Scan(ProfileOver(_root), TriggerKind.Cli));

        Assert.Empty(faults);
        Assert.Equal(2, payloads.Count);
        Assert.Contains(payloads, p => p.SourcePath == nested);
        Assert.All(payloads, p => Assert.Equal(Path.TrimEndingDirectorySeparator(_root), p.SourceRoot));
    }

    [Fact]
    public void Max_depth_prunes_descent()
    {
        Touch("root.txt");                     // depth 0
        Touch("l1", "one.txt");                // depth 1
        Touch("l1", "l2", "two.txt");          // depth 2 — pruned

        var profile = ProfileOver(_root, new FilterSet { MaxDepth = 1 });
        var (payloads, _) = Collect(NewScanner().Scan(profile, TriggerKind.Cli));

        Assert.Equal(2, payloads.Count);
        Assert.DoesNotContain(payloads, p => p.SourcePath.EndsWith("two.txt"));
    }

    [Fact]
    public void Infrastructure_directories_and_temp_files_are_never_scanned()
    {
        Touch("keep.txt");
        Touch(".pipeline_tmp", "job1", "workspace.txt");
        Touch(".fm_staging", "job2", "staged.txt");
        Touch("song.flac.fmtmp-abc12345");

        var (payloads, _) = Collect(NewScanner().Scan(ProfileOver(_root), TriggerKind.Cli));

        Assert.Single(payloads);
        Assert.EndsWith("keep.txt", payloads[0].SourcePath);
    }

    [Fact]
    public void Scope_narrows_to_a_subtree_under_the_source()
    {
        Touch("outside.txt");
        string inside = Touch("scoped", "inside.txt");

        var (payloads, faults) = Collect(NewScanner().Scan(
            ProfileOver(_root), TriggerKind.Cli, Path.Combine(_root, "scoped")));

        Assert.Empty(faults);
        Assert.Single(payloads);
        Assert.Equal(inside, payloads[0].SourcePath);
    }

    [Fact]
    public void File_scope_yields_exactly_that_file()
    {
        string file = Touch("only.txt");
        Touch("other.txt");

        var (payloads, _) = Collect(NewScanner().Scan(ProfileOver(_root), TriggerKind.Cli, file));

        Assert.Single(payloads);
        Assert.Equal(file, payloads[0].SourcePath);
    }

    [Fact]
    public void Scope_outside_every_source_is_fatal()
    {
        var (payloads, faults) = Collect(NewScanner().Scan(
            ProfileOver(_root), TriggerKind.Cli, Path.Combine(Path.GetTempPath(), "definitely-elsewhere")));

        Assert.Empty(payloads);
        Assert.Contains(faults, f => f.Severity == EnumerationSeverity.Fatal);
    }

    [Fact]
    public void Missing_source_root_is_fatal()
    {
        var (payloads, faults) = Collect(NewScanner().Scan(
            ProfileOver(Path.Combine(_root, "does-not-exist")), TriggerKind.Cli));

        Assert.Empty(payloads);
        Assert.Contains(faults, f => f.Severity == EnumerationSeverity.Fatal);
    }
}
