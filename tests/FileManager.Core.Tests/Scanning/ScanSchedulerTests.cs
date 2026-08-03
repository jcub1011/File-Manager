using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Scanning;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Scanning;

/// <summary>Direct tests of the process-wide scan scheduler's novel guarantees — the per-drive cap it
/// enforces and the per-session backpressure that keeps one session's stalled consumer from blocking
/// another. Uses an in-memory, optionally blocking file system so concurrency is observable.</summary>
public sealed class ScanSchedulerTests
{
    [Fact]
    public void Per_volume_cap_bounds_concurrent_enumeration()
    {
        const int cap = 2, roots = 6;
        using ManualResetEventSlim gate = new(initialState: false);   // block every EnumerateEntries
        Dictionary<string, FileSystemEntry[]> tree = [];
        for (int i = 0; i < roots; i++)
            tree[$"v:\\d{i}"] = [FileEntry($"v:\\d{i}\\f.txt")];
        BlockingFileSystem fs = new(tree, gate);

        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 32, perDrive: cap));
        IScanSession session = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        for (int i = 0; i < roots; i++)
            session.Submit(new ScanWorkItem($"v:\\d{i}", "v:", DriveClass.Fixed, null));
        session.CompleteSubmissions();

        List<ScanResult> results = [];
        Task consumer = Task.Run(() =>
        {
            foreach (ScanResult r in session.Consume())
                results.Add(r);
        });

        // The cap should saturate (proving real parallelism) but never be exceeded (the guarantee).
        Assert.True(SpinWait.SpinUntil(() => fs.Live >= cap, TimeSpan.FromSeconds(5)), "cap never saturated");
        Thread.Sleep(150);   // give any (buggy) extra worker a chance to break the cap
        Assert.True(fs.MaxConcurrent <= cap, $"observed {fs.MaxConcurrent} concurrent enumerations > cap {cap}");

        gate.Set();
        Assert.True(consumer.Wait(TimeSpan.FromSeconds(10)), "scan did not complete after release");
        session.Dispose();
        Assert.Equal(roots, results.Count);   // one file per root, all delivered
    }

    [Fact]
    public void A_stalled_consumer_does_not_block_another_session()
    {
        using ManualResetEventSlim open = new(initialState: true);   // never blocks
        Dictionary<string, FileSystemEntry[]> tree = new()
        {
            ["a:\\dir"] = [.. Enumerable.Range(0, 20).Select(i => FileEntry($"a:\\dir\\f{i}.txt"))],
            ["b:\\dir"] = [.. Enumerable.Range(0, 5).Select(i => FileEntry($"b:\\dir\\f{i}.txt"))],
        };
        BlockingFileSystem fs = new(tree, open);

        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 32, perDrive: 32));

        // Session A has a tiny output buffer and is left unconsumed, so its work fills the buffer and
        // parks. Session B must still complete despite A hogging nothing but its own parked buffer.
        using IScanSession a = scheduler.OpenSession(EmitEverything() with { OutputCapacity = 2 }, CancellationToken.None);
        a.Submit(new ScanWorkItem("a:\\dir", "a:", DriveClass.Fixed, null));
        a.CompleteSubmissions();

        using IScanSession b = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        b.Submit(new ScanWorkItem("b:\\dir", "b:", DriveClass.Fixed, null));
        b.CompleteSubmissions();

        // Drain B fully WITHOUT touching A. If A's full buffer stalled shared workers, this would hang.
        List<ScanResult> bResults = [];
        Task bConsumer = Task.Run(() => bResults.AddRange(b.Consume()));
        Assert.True(bConsumer.Wait(TimeSpan.FromSeconds(10)), "the second session was blocked by the stalled one");
        Assert.Equal(5, bResults.Count);

        // Now drain A — the parked work resumes as the consumer frees buffer space.
        List<ScanResult> aResults = [.. a.Consume()];
        Assert.Equal(20, aResults.Count);
    }

    [Fact]
    public void Cancelling_one_session_leaves_another_intact()
    {
        using ManualResetEventSlim open = new(initialState: true);
        Dictionary<string, FileSystemEntry[]> tree = new()
        {
            ["b:\\dir"] = [.. Enumerable.Range(0, 5).Select(i => FileEntry($"b:\\dir\\f{i}.txt"))],
        };
        BlockingFileSystem fs = new(tree, open);
        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 32, perDrive: 32));

        using CancellationTokenSource cts = new();
        IScanSession a = scheduler.OpenSession(EmitEverything(), cts.Token);
        a.Submit(new ScanWorkItem("missing-in-tree:\\dir", "x:", DriveClass.Fixed, null));
        a.CompleteSubmissions();
        cts.Cancel();   // cancel A: its Consume must terminate

        List<ScanResult> aResults = [.. a.Consume()];   // completes (empty or partial), does not hang

        using IScanSession b = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        b.Submit(new ScanWorkItem("b:\\dir", "b:", DriveClass.Fixed, null));
        b.CompleteSubmissions();
        List<ScanResult> bResults = [.. b.Consume()];
        Assert.Equal(5, bResults.Count);   // B is unaffected by A's cancellation
    }

    [Fact]
    public void A_root_submitted_after_an_earlier_root_drains_is_not_dropped()
    {
        // The drain race: adapters submit roots with I/O between the Submits (volume resolution),
        // so an empty/fast first root can fully complete while the second is still on its way. The
        // session must not finalize until CompleteSubmissions — finalizing on outstanding==0 alone
        // silently drops every later root from the scan.
        using ManualResetEventSlim open = new(initialState: true);
        Dictionary<string, FileSystemEntry[]> tree = new()
        {
            // "a:\\empty" is deliberately absent: enumerating it yields nothing, completing instantly.
            ["b:\\dir"] = [.. Enumerable.Range(0, 5).Select(i => FileEntry($"b:\\dir\\f{i}.txt"))],
        };
        BlockingFileSystem fs = new(tree, open);
        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 32, perDrive: 32));

        using IScanSession session = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        session.Submit(new ScanWorkItem("a:\\empty", "a:", DriveClass.Fixed, null));
        // Force the race shape: the first root has been fully enumerated and its worker has gone
        // idle before the second root is submitted.
        Assert.True(SpinWait.SpinUntil(() => fs.EnumeratedPaths.Contains("a:\\empty") && fs.Live == 0,
            TimeSpan.FromSeconds(5)), "first root never enumerated");
        Thread.Sleep(150);   // let the worker's Complete() bookkeeping land (outstanding → 0)

        session.Submit(new ScanWorkItem("b:\\dir", "b:", DriveClass.Fixed, null));
        session.CompleteSubmissions();

        List<ScanResult> results = [.. session.Consume()];
        Assert.Equal(5, results.Count);   // the late root's files all arrive
    }

    [Fact]
    public void Submitting_a_root_after_the_session_drained_fails_loud()
    {
        using ManualResetEventSlim open = new(initialState: true);
        BlockingFileSystem fs = new(new Dictionary<string, FileSystemEntry[]>(), open);
        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 32, perDrive: 32));

        using IScanSession session = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        session.Submit(new ScanWorkItem("a:\\empty", "a:", DriveClass.Fixed, null));
        session.CompleteSubmissions();
        List<ScanResult> results = [.. session.Consume()];   // drains: session is finalized
        Assert.Empty(results);

        // A root pushed now would land on a removed session and vanish — it must throw instead.
        Assert.Throws<InvalidOperationException>(() =>
            session.Submit(new ScanWorkItem("b:\\late", "b:", DriveClass.Fixed, null)));
    }

    [Fact]
    public void A_directory_tree_that_loops_back_on_itself_terminates_at_the_depth_ceiling()
    {
        // THE cycle test. Neither adapter's never-descend-a-reparse-point rule can see this loop: the
        // repeating child is an ordinary directory with NO reparse attribute, which is exactly how a
        // server-side symlink on an SMB/NFS share and a looping DFS link reach the client. Nothing else
        // bounds the walk — the destination sweep has no depth policy at all and its entry budget gates
        // emission, not descent — so without the engine ceiling this call never returns.
        LoopingFileSystem fs = new(branches: 1);
        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 4, perDrive: 4, maxDepth: 8));

        using IScanSession session = scheduler.OpenSession(DescendEverything(), CancellationToken.None);
        session.Submit(new ScanWorkItem("v:\\root", "v:", DriveClass.Fixed, null));
        session.CompleteSubmissions();

        List<ScanResult> results = Drain(session);

        // Depth 0 (the root) through depth 8 inclusive = 9 directories, one file each.
        Assert.Equal(9, results.Count(r => r.Entry is not null));
        Assert.Equal(9, fs.EnumeratedPaths.Count);
        // ...and the truncation is reported rather than silent, so a looping share is diagnosable.
        ScanResult result = Assert.Single(results, r => r.Fault is not null);
        EnumerationFault fault = result.Fault!.Value;
        Assert.Equal(EnumerationSeverity.Warning, fault.Severity);
        Assert.Contains("depth ceiling", fault.Message);
    }

    [Fact]
    public void The_configured_depth_ceiling_is_what_bounds_the_walk()
    {
        // Pins the setting to the behavior: the ceiling comes from GlobalSettings, not a constant.
        LoopingFileSystem fs = new(branches: 1);
        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 4, perDrive: 4, maxDepth: 20));

        using IScanSession session = scheduler.OpenSession(DescendEverything(), CancellationToken.None);
        session.Submit(new ScanWorkItem("v:\\root", "v:", DriveClass.Fixed, null));
        session.CompleteSubmissions();

        Assert.Equal(21, Drain(session).Count(r => r.Entry is not null));   // depths 0..20
    }

    [Fact]
    public void A_branching_loop_reports_the_depth_ceiling_once_not_per_pruned_directory()
    {
        // A loop re-expands the full subtree breadth at every level, so the ceiling is hit once per
        // directory ON the boundary — 512 times here. One warning is the whole message; 512 would bury
        // both the report and the log.
        LoopingFileSystem fs = new(branches: 2);
        using ScanScheduler scheduler = new(
            NullLogger<ScanScheduler>.Instance, fs, Settings(maxScan: 4, perDrive: 4, maxDepth: 8));

        using IScanSession session = scheduler.OpenSession(DescendEverything(), CancellationToken.None);
        session.Submit(new ScanWorkItem("v:\\root", "v:", DriveClass.Fixed, null));
        session.CompleteSubmissions();

        List<ScanResult> results = Drain(session);

        Assert.Equal(511, results.Count(r => r.Entry is not null));   // 2^0 + ... + 2^8 directories
        Assert.Single(results, r => r.Fault is not null);
    }

    /// <summary>Drains a session on a worker thread with a hard timeout, so a walk that fails to
    /// terminate fails the test instead of hanging the run — the whole point of these cases.</summary>
    private static List<ScanResult> Drain(IScanSession session)
    {
        List<ScanResult> results = [];
        Task consumer = Task.Run(() => results.AddRange(session.Consume()));
        Assert.True(consumer.Wait(TimeSpan.FromSeconds(30)), "the scan did not terminate — the depth ceiling did not bound it");
        return results;
    }

    private static ScanSessionOptions EmitEverything() => new()
    {
        OnSubdirectory = static (_, _) => new ChildDecision(false, null),
        OnFile = static (_, _) => true,
    };

    /// <summary>The unguarded adapter: descends every subdirectory with no reparse, depth, or name
    /// policy of its own, leaving the scheduler's ceiling as the only bound.</summary>
    private static ScanSessionOptions DescendEverything() => new()
    {
        MaxConcurrency = 1,   // serial: the assertions are on exact counts
        OnSubdirectory = static (_, tag) => new ChildDecision(true, tag),
        OnFile = static (_, _) => true,
    };

    private static FakeSettingsProvider Settings(int maxScan, int perDrive, int? maxDepth = null) => new(new GlobalSettings
    {
        MaxScanDepth = maxDepth ?? GlobalSettings.DefaultMaxScanDepth,
        ScanThreading = new ScanThreadingSettings
        {
            MaxScanThreads = ThreadBudget.Explicit(maxScan),
            PerDriveDefault = ThreadBudget.Explicit(perDrive),
        },
    });

    private static FileSystemEntry FileEntry(string path) =>
        new(System.IO.Path.GetFileName(path), path, isDirectory: false, size: 1, modified: DateTimeOffset.UnixEpoch);

    /// <summary>A file system with no bottom: every directory enumerates as one file plus
    /// <paramref name="branches"/> subdirectories that enumerate the same way, forever. The
    /// subdirectory entries carry NO attributes — in particular not
    /// <see cref="System.IO.FileAttributes.ReparsePoint"/> — which is the case the adapters' guards
    /// cannot catch and the engine's depth ceiling exists for.</summary>
    private sealed class LoopingFileSystem(int branches) : IFileSystemService
    {
        public System.Collections.Concurrent.ConcurrentBag<string> EnumeratedPaths { get; } = [];

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateEntries(string path)
        {
            EnumeratedPaths.Add(path);
            yield return FileEntry($"{path}\\f.txt");
            for (int i = 0; i < branches; i++)
            {
                string child = $"{path}\\loop{i}";
                yield return new FileSystemEntry(
                    $"loop{i}", child, isDirectory: true, size: 0, modified: DateTimeOffset.UnixEpoch);
            }
        }

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateRoots() => throw new NotSupportedException();
        public Result<string, string> GetHomeDirectory() => throw new NotSupportedException();
        public Result<string?, string> GetParent(string path) => throw new NotSupportedException();
    }

    /// <summary>An in-memory file system whose <see cref="EnumerateEntries"/> blocks on a shared gate
    /// (so workers pile up on a volume) and tracks the peak concurrent-enumeration count.</summary>
    private sealed class BlockingFileSystem(Dictionary<string, FileSystemEntry[]> tree, ManualResetEventSlim gate) : IFileSystemService
    {
        private int _live;
        private int _max;

        public int Live => Volatile.Read(ref _live);
        public int MaxConcurrent => Volatile.Read(ref _max);
        public System.Collections.Concurrent.ConcurrentBag<string> EnumeratedPaths { get; } = [];

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateEntries(string path)
        {
            EnumeratedPaths.Add(path);
            int now = Interlocked.Increment(ref _live);
            try
            {
                int seen;
                while (now > (seen = Volatile.Read(ref _max)))
                    Interlocked.CompareExchange(ref _max, now, seen);
                gate.Wait();
                if (tree.TryGetValue(path, out FileSystemEntry[]? entries))
                    foreach (FileSystemEntry entry in entries)
                        yield return entry;
            }
            finally
            {
                Interlocked.Decrement(ref _live);
            }
        }

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateRoots() => throw new NotSupportedException();
        public Result<string, string> GetHomeDirectory() => throw new NotSupportedException();
        public Result<string?, string> GetParent(string path) => throw new NotSupportedException();
    }
}
