using FileManager.Contracts.DryRun;
using FileManager.Core.DryRun;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.DryRun;

public sealed class FileDryRunSpoolTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "fm-spool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
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

    private static void AssertEntryEqual(FileEvaluation expected, FileEvaluation actual)
    {
        Assert.Equal(expected.SourceFile, actual.SourceFile);       // records: value equality on scalars
        Assert.Equal(expected.SourceOp, actual.SourceOp);
        Assert.Equal(expected.DestinationFiles, actual.DestinationFiles);
        Assert.Equal(expected.DestinationOps, actual.DestinationOps);
    }

    private async Task<List<FileEvaluation>> WriteReadAsync(IDryRunSpool spool, IReadOnlyList<FileEvaluation> entries)
    {
        foreach (FileEvaluation e in entries)
            await spool.WriteAsync(e, default);
        await spool.CompleteWritingAsync();
        List<FileEvaluation> read = [];
        await foreach (FileEvaluation e in spool.ReadAllAsync(default))
            read.Add(e);
        return read;
    }

    [Fact]
    public async Task Small_run_stays_in_memory_and_never_touches_disk()
    {
        List<FileEvaluation> entries = [.. Enumerable.Range(0, 20).Select(Entry)];
        // A generous threshold so this modest set never spills.
        await using FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 1 << 20, NullLogger.Instance);

        List<FileEvaluation> read = await WriteReadAsync(spool, entries);

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
        FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 8, NullLogger.Instance);

        foreach (FileEvaluation e in entries)
            await spool.WriteAsync(e, default);
        await spool.CompleteWritingAsync();

        Assert.True(Directory.EnumerateFiles(_scratch, "*.snapshot").Any(),
            "an above-threshold run must spill to a snapshot file");

        List<FileEvaluation> read = [];
        await foreach (FileEvaluation e in spool.ReadAllAsync(default))
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
        FileDryRunSpool spool = new(_scratch, spillThresholdBytes: 8, NullLogger.Instance);
        foreach (FileEvaluation e in Enumerable.Range(0, 50).Select(Entry))
            await spool.WriteAsync(e, default);
        await spool.CompleteWritingAsync();
        Assert.True(Directory.EnumerateFiles(_scratch, "*.snapshot").Any());

        // Abandon mid-run (the cancel path): disposal must still purge the file.
        await spool.DisposeAsync();
        Assert.Empty(Directory.EnumerateFiles(_scratch, "*.snapshot"));
    }
}
