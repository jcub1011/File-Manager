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

        using IScanSession b = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        b.Submit(new ScanWorkItem("b:\\dir", "b:", DriveClass.Fixed, null));

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
        cts.Cancel();   // cancel A: its Consume must terminate

        List<ScanResult> aResults = [.. a.Consume()];   // completes (empty or partial), does not hang

        using IScanSession b = scheduler.OpenSession(EmitEverything(), CancellationToken.None);
        b.Submit(new ScanWorkItem("b:\\dir", "b:", DriveClass.Fixed, null));
        List<ScanResult> bResults = [.. b.Consume()];
        Assert.Equal(5, bResults.Count);   // B is unaffected by A's cancellation
    }

    private static ScanSessionOptions EmitEverything() => new()
    {
        OnSubdirectory = static (_, _) => new ChildDecision(false, null),
        OnFile = static (_, _) => true,
    };

    private static FakeSettingsProvider Settings(int maxScan, int perDrive) => new(new GlobalSettings
    {
        ScanThreading = new ScanThreadingSettings
        {
            MaxScanThreads = ThreadBudget.Explicit(maxScan),
            PerDriveDefault = ThreadBudget.Explicit(perDrive),
        },
    });

    private static FileSystemEntry FileEntry(string path) =>
        new(System.IO.Path.GetFileName(path), path, IsDirectory: false, Size: 1, Modified: DateTimeOffset.UnixEpoch);

    /// <summary>An in-memory file system whose <see cref="EnumerateEntries"/> blocks on a shared gate
    /// (so workers pile up on a volume) and tracks the peak concurrent-enumeration count.</summary>
    private sealed class BlockingFileSystem(Dictionary<string, FileSystemEntry[]> tree, ManualResetEventSlim gate) : IFileSystemService
    {
        private int _live;
        private int _max;

        public int Live => Volatile.Read(ref _live);
        public int MaxConcurrent => Volatile.Read(ref _max);

        public IEnumerable<Result<FileSystemEntry, EnumerationFault>> EnumerateEntries(string path)
        {
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
