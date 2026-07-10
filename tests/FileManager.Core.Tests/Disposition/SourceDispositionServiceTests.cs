using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Disposition;

public sealed class SourceDispositionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly EnginePaths _paths;
    private readonly DispositionAuditLog _audit;

    public SourceDispositionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-disp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new EnginePaths { Root = Path.Combine(_root, "engine") };
        _audit = new DispositionAuditLog(_paths, NullLogger<DispositionAuditLog>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (SourceDispositionService Service, FakeTrashService Trash) Service()
    {
        var trash = new FakeTrashService(Path.Combine(_root, "bin"));
        var service = new SourceDispositionService(trash, _audit, new FakeTimeProvider(), NullLogger<SourceDispositionService>.Instance);
        return (service, trash);
    }

    private JobExecution ExecutionFor(string source, OnSuccessAction action, string? archive = null, TargetState targetState = TargetState.Placed)
    {
        string targetRoot = Path.Combine(_root, "target");
        JobExecution execution = JobFixtures.Execution(
            source, _root, [Path.Combine(targetRoot, "f")], [targetRoot],
            JobFixtures.Policy(onSuccess: action, archiveFolder: archive));
        execution.Targets[0].State = targetState;
        return execution;
    }

    [Fact]
    public void KeepSource_leaves_the_file_and_writes_no_audit_record()
    {
        string source = Path.Combine(_root, "keep.txt");
        File.WriteAllText(source, "x");
        (SourceDispositionService service, _) = Service();

        var result = service.Dispose(ExecutionFor(source, OnSuccessAction.KeepSource));
        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(_paths.AuditDirectory));   // nothing appended for a non-deletion
    }

    [Fact]
    public void MoveToTrash_recycles_the_source_and_audits_it()
    {
        string source = Path.Combine(_root, "trash.txt");
        File.WriteAllText(source, "x");
        (SourceDispositionService service, FakeTrashService trash) = Service();

        var result = service.Dispose(ExecutionFor(source, OnSuccessAction.MoveToTrash));
        Assert.True(result.TryGetValue(out DispositionAuditRecord? record));
        Assert.Equal(OnSuccessAction.MoveToTrash, record.Action);
        Assert.Contains(source, trash.Trashed);
        Assert.False(File.Exists(source));

        _audit.ReadRecent(10).TryGetValue(out IReadOnlyList<DispositionAuditRecord>? audited);
        Assert.Single(audited!);
    }

    [Fact]
    public void PermanentDelete_removes_the_source()
    {
        string source = Path.Combine(_root, "gone.txt");
        File.WriteAllText(source, "x");
        (SourceDispositionService service, _) = Service();

        service.Dispose(ExecutionFor(source, OnSuccessAction.PermanentDelete));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void A_skipped_conflict_downgrades_a_disposing_action_to_keep()
    {
        string source = Path.Combine(_root, "kept-due-to-skip.txt");
        File.WriteAllText(source, "x");
        (SourceDispositionService service, FakeTrashService trash) = Service();

        var result = service.Dispose(ExecutionFor(source, OnSuccessAction.PermanentDelete, targetState: TargetState.SkippedConflict));
        Assert.True(result.TryGetValue(out DispositionAuditRecord? record));
        Assert.Equal(OnSuccessAction.KeepSource, record.Action);   // downgraded
        Assert.True(File.Exists(source));                          // not deleted
        Assert.Empty(trash.Trashed);
    }

    [Fact]
    public void Recovery_variant_treats_an_already_gone_source_as_disposed()
    {
        (SourceDispositionService service, _) = Service();
        var source = new SourceSnapshot { Path = Path.Combine(_root, "never-existed.txt"), SizeBytes = 0, LastWriteUtc = DateTimeOffset.UnixEpoch };

        var result = service.Dispose(Guid.NewGuid(), source, JobFixtures.Policy(onSuccess: OnSuccessAction.PermanentDelete), anyTargetSkippedConflict: false);
        Assert.True(result.IsSuccess);   // idempotent — no error though the file is absent
    }
}
