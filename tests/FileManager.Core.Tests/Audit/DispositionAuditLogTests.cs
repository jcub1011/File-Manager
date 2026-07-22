using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Audit;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Audit;

public sealed class DispositionAuditLogTests : IDisposable
{
    private readonly string _root;
    private readonly DispositionAuditLog _audit;

    public DispositionAuditLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _audit = new DispositionAuditLog(new EnginePaths { Root = _root }, NullLogger<DispositionAuditLog>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Appends_and_reads_recent_newest_first()
    {
        var t = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        _audit.Append(new DispositionAuditRecord(Guid.NewGuid(), @"C:\a.txt", OnSuccessAction.MoveToTrash, "RecycleBin", t));
        _audit.Append(new DispositionAuditRecord(Guid.NewGuid(), @"C:\b.txt", OnSuccessAction.PermanentDelete, null, t.AddMinutes(5)));

        Result<IReadOnlyList<DispositionAuditRecord>, string> read = _audit.ReadRecent(10);
        Assert.True(read.TryGetValue(out IReadOnlyList<DispositionAuditRecord>? records));
        Assert.Equal(2, records.Count);
        Assert.Equal(@"C:\b.txt", records[0].SourcePath);   // newest first
        Assert.Equal(@"C:\a.txt", records[1].SourcePath);
    }

    [Fact]
    public void Reading_an_empty_log_returns_no_records()
    {
        Result<IReadOnlyList<DispositionAuditRecord>, string> read = _audit.ReadRecent(10);
        Assert.True(read.TryGetValue(out IReadOnlyList<DispositionAuditRecord>? records));
        Assert.Empty(records);
    }
}
