using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Scanning;
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

    // A scan scheduler backed by the real file system. When maxThreads is given, the global and
    // per-drive budgets are pinned to it (1 = serial), letting a test compare concurrency levels.
    private static SourceScanner NewScanner(int? maxThreads = null)
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
        return new SourceScanner(TimeProvider.System, scheduler);
    }

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
    public void A_directory_junction_is_never_descended()
    {
        // A junction inside the source can cycle back into an ancestor (unbounded re-walk) or reach
        // files OUTSIDE the source root, which would become disposition candidates far beyond the
        // tree the profile configured. The scan must not follow it.
        Touch("keep.txt");
        string outside = Path.Combine(Path.GetTempPath(), "fm-scanner-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "escape.txt"), "x");
            string junction = Path.Combine(_root, "jump");
            // mklink /J needs no elevation; if the environment still refuses, skip rather than fail.
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            mklink.WaitForExit();
            if (mklink.ExitCode != 0 || !Directory.Exists(junction))
                return;   // junction creation unavailable here — nothing to assert

            try
            {
                var (payloads, faults) = Collect(NewScanner().Scan(ProfileOver(_root), TriggerKind.Cli));

                Assert.Empty(faults);
                Assert.Single(payloads);
                Assert.EndsWith("keep.txt", payloads[0].SourcePath);   // escape.txt never reached
            }
            finally
            {
                // Remove the junction itself before the fixture's recursive delete of _root — a
                // (soon-dangling) junction in the tree breaks Directory.Delete(recursive).
                Directory.Delete(junction, recursive: false);
            }
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
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

    [Fact]
    public void Worker_count_does_not_change_the_payload_set()
    {
        // A deep, wide tree so the parallel walk genuinely interleaves across workers.
        for (int d = 0; d < 8; d++)
            for (int f = 0; f < 12; f++)
                Touch($"dir{d}", $"sub{f}", $"file{d}_{f}.txt");

        var profile = ProfileOver(_root);
        var (serial, serialFaults) = Collect(NewScanner(maxThreads: 1).Scan(profile, TriggerKind.Cli));
        var (parallel, parallelFaults) = Collect(NewScanner(maxThreads: 8).Scan(profile, TriggerKind.Cli));

        Assert.Empty(serialFaults);
        Assert.Empty(parallelFaults);
        Assert.Equal(96, serial.Count);
        // Emission order is non-deterministic under parallelism, so compare as sets of source paths.
        Assert.Equal(
            serial.Select(p => p.SourcePath).OrderBy(p => p, StringComparer.Ordinal),
            parallel.Select(p => p.SourcePath).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Early_break_tears_the_walk_down_without_hanging()
    {
        for (int d = 0; d < 6; d++)
            for (int f = 0; f < 20; f++)
                Touch($"d{d}", $"file{d}_{f}.txt");

        // Consume only the first few payloads, then break — the enumerator's disposal must cancel
        // the producer threads and unblock the bounded buffer rather than deadlock.
        Task<int> scan = Task.Run(() =>
        {
            int seen = 0;
            foreach (var result in NewScanner(maxThreads: 8).Scan(ProfileOver(_root), TriggerKind.Cli))
            {
                if (result.TryGetValue(out _) && ++seen >= 3)
                    break;
            }
            return seen;
        });

        Task completed = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(scan, completed);   // the scan tore down before the timeout fired
        Assert.Equal(3, await scan);
    }

    [Fact]
    public void Cancellation_throws_from_the_enumerator()
    {
        Touch("a.txt");
        Touch("b.txt");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var _ in NewScanner().Scan(ProfileOver(_root), TriggerKind.Cli, ct: cts.Token))
            {
            }
        });
    }
}
