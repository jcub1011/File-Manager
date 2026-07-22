using FileManager.Contracts.DryRun;
using FileManager.Core.DryRun;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace FileManager.Core.Tests.DryRun;

public sealed class FileDryRunSpoolTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "fm-spool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
        try { File.Delete(_scratch); } catch { /* best effort — some tests plant a file at the scratch path */ }
    }

    private static FileEvaluation Entry(int i)
    {
        PhysicalFile source = new()
        {
            Path = $@"C:\src\file{i}.txt",
            Root = @"C:\src",
            Length = i,
            LastWritten = DateTimeOffset.UnixEpoch.AddSeconds(i),
            IsReparsePoint = i % 2 == 0,
        };
        VirtualFileOperation sourceOp = new()
        {
            Path = source.Path,
            Root = source.Root,
            Kind = OperationKind.Processed,
            Detail = $"detail {i}",
        };
        PhysicalFile dest = new() { Path = $@"C:\dst\file{i}.txt", Root = @"C:\dst", Length = i, LastWritten = source.LastWritten };
        VirtualFileOperation destOp = new()
        {
            Path = dest.Path, Root = dest.Root, Kind = OperationKind.New, SourceIndex = 0, SubjectIndex = -1,
        };
        return new FileEvaluation(source, sourceOp, [dest], [destOp]);
    }

    // ReadAllAsync yields IEvaluationView: original records on the in-memory/non-spilled path, pooled
    // carriers on the spilled path. Comparing field-by-field via the view covers both uniformly.
    private static void AssertEntryEqual(FileEvaluation expected, IEvaluationView actual)
    {
        AssertFileEqual(expected.SourceFile, actual.SourceFile);
        AssertOpEqual(expected.SourceOp, actual.SourceOp);
        Assert.Equal(expected.DestinationFiles.Count, actual.DestinationFiles.Count);
        for (int i = 0; i < expected.DestinationFiles.Count; i++)
            AssertFileEqual(expected.DestinationFiles[i], actual.DestinationFiles[i]);
        Assert.Equal(expected.DestinationOps.Count, actual.DestinationOps.Count);
        for (int i = 0; i < expected.DestinationOps.Count; i++)
            AssertOpEqual(expected.DestinationOps[i], actual.DestinationOps[i]);
    }

    private static void AssertFileEqual(IPhysicalFileView expected, IPhysicalFileView actual)
    {
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.LastWritten, actual.LastWritten);
        Assert.Equal(expected.IsReparsePoint, actual.IsReparsePoint);
    }

    private static void AssertOpEqual(IFileOperationView expected, IFileOperationView actual)
    {
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Root, actual.Root);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.SourceIndex, actual.SourceIndex);
        Assert.Equal(expected.SubjectIndex, actual.SubjectIndex);
        Assert.Equal(expected.SourceDisposition, actual.SourceDisposition);
        Assert.Equal(expected.Detail, actual.Detail);
    }

    private async Task<List<IEvaluationView>> WriteReadAsync(IDryRunSpool spool, IReadOnlyList<FileEvaluation> entries)
    {
        foreach (FileEvaluation e in entries)
            await spool.WriteAsync(e, default);
        await spool.CompleteWritingAsync();
        List<IEvaluationView> read = [];
        await foreach (IEvaluationView e in spool.ReadAllAsync(default))
            read.Add(e);
        return read;
    }

    [Fact]
    public async Task Small_run_stays_in_memory_and_never_touches_disk()
    {
        List<FileEvaluation> entries = [.. Enumerable.Range(0, 20).Select(Entry)];
        // A generous threshold so this modest set never spills.
        await using FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 1 << 20, new EvaluationCarrierPool(), NullLogger.Instance);

        List<IEvaluationView> read = await WriteReadAsync(spool, entries);

        Assert.Equal(entries.Count, read.Count);
        for (int i = 0; i < entries.Count; i++)
            AssertEntryEqual(entries[i], read[i]);
        Assert.False(Directory.Exists(_scratch) && Directory.EnumerateFiles(_scratch, "*.snapshot").Any(),
            "a below-threshold run must not create a snapshot file");
    }

    [Fact]
    public async Task Large_run_spills_to_a_snapshot_file_and_replays_in_write_order()
    {
        List<FileEvaluation> entries = [.. Enumerable.Range(0, 200).Select(Entry)];
        // A tiny threshold forces a spill almost immediately.
        FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 8, new EvaluationCarrierPool(), NullLogger.Instance);

        foreach (FileEvaluation e in entries)
            await spool.WriteAsync(e, default);
        await spool.CompleteWritingAsync();

        Assert.True(Directory.EnumerateFiles(_scratch, "*.snapshot").Any(),
            "an above-threshold run must spill to a snapshot file");

        // Spilled read yields pooled carriers; the test never recycles, so each entry is a distinct
        // rented instance and collecting them is safe.
        List<IEvaluationView> read = [];
        await foreach (IEvaluationView e in spool.ReadAllAsync(default))
            read.Add(e);

        Assert.Equal(entries.Count, read.Count);
        for (int i = 0; i < entries.Count; i++)
            AssertEntryEqual(entries[i], read[i]);   // discovery/write order preserved

        await spool.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(_scratch, "*.snapshot"));   // deleted on dispose
    }

    [Fact]
    public async Task Dispose_without_completing_deletes_the_snapshot_file()
    {
        FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 8, new EvaluationCarrierPool(), NullLogger.Instance);
        foreach (FileEvaluation e in Enumerable.Range(0, 50).Select(Entry))
            await spool.WriteAsync(e, default);

        // CompleteWritingAsync is deliberately NOT called — this is the abandon/cancel path. The
        // writer drains asynchronously, so wait for the spill to hit disk before disposing.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!Directory.Exists(_scratch) || !Directory.EnumerateFiles(_scratch, "*.snapshot").Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "the spill never produced a snapshot file");
            await Task.Delay(10);
        }

        // Abandon mid-run: disposal must join the writer and still purge the file.
        await spool.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(_scratch, "*.snapshot"));
    }

    [Fact]
    public async Task Writer_failure_unblocks_producers_and_surfaces_from_CompleteWritingAsync()
    {
        // A *file* at the scratch path makes the spill's Directory.CreateDirectory throw, so the
        // writer faults on the first record. The fault must fail the channel: before the fix the
        // bounded channel (capacity 1024) filled with nobody draining and every producer blocked
        // forever in WriteAsync — this test deadlocked instead of finishing.
        File.WriteAllText(_scratch, "not a directory");
        await using FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 1, new EvaluationCarrierPool(), NullLogger.Instance);

        // Far more writes than the channel capacity. Writes are accepted until the writer faults,
        // then throw ChannelClosedException carrying the root cause.
        ChannelClosedException? closed = null;
        await Task.Run(async () =>
        {
            for (int i = 0; i < 3000 && closed is null; i++)
            {
                try { await spool.WriteAsync(Entry(i), default); }
                catch (ChannelClosedException ex) { closed = ex; }
            }
        }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(closed);   // producers get the fault, not a hang
        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () => await spool.CompleteWritingAsync());
        Assert.NotNull(surfaced.InnerException);   // the root cause is preserved
    }
}
