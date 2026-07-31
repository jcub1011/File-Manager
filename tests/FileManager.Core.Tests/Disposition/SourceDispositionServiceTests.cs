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

    // ---- MoveToArchive: the action that RELOCATES the user's original -----------------------------

    /// <summary>Archive destination resolution reads the profile's TargetLayout and the payload's
    /// source root, so these need a plan built around a real source tree.</summary>
    private JobExecution ArchiveExecutionFor(string source, string sourceRoot, string archive, TargetLayout layout)
    {
        string targetRoot = Path.Combine(_root, "target");
        Profile profile = TestProfiles.Valid(sourceRoot, targetRoot) with { TargetLayout = layout };
        JobExecution execution = JobFixtures.Execution(
            source, sourceRoot, [Path.Combine(targetRoot, "f")], [targetRoot],
            JobFixtures.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: archive),
            profile: profile);
        execution.Targets[0].State = TargetState.Placed;
        return execution;
    }

    [Fact]
    public void MoveToArchive_under_Flatten_places_the_original_in_the_archive_root()
    {
        string sourceRoot = Path.Combine(_root, "src");
        string source = Path.Combine(sourceRoot, "deep", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "original");
        string archive = Path.Combine(_root, "archive");
        (SourceDispositionService service, _) = Service();

        var result = service.Dispose(ArchiveExecutionFor(source, sourceRoot, archive, TargetLayout.Flatten));

        Assert.True(result.TryGetValue(out DispositionAuditRecord? record));
        Assert.Equal(OnSuccessAction.MoveToArchive, record!.Action);
        string archived = Path.Combine(archive, "photo.jpg");
        Assert.Equal(archived, record.Destination);
        Assert.Equal("original", File.ReadAllText(archived));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void MoveToArchive_under_PreserveStructure_mirrors_the_source_relative_path()
    {
        string sourceRoot = Path.Combine(_root, "src");
        string relative = Path.Combine("2026", "07", "photo.jpg");
        string source = Path.Combine(sourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "original");
        string archive = Path.Combine(_root, "archive");
        (SourceDispositionService service, _) = Service();

        service.Dispose(ArchiveExecutionFor(source, sourceRoot, archive, TargetLayout.PreserveStructure));

        Assert.Equal("original", File.ReadAllText(Path.Combine(archive, relative)));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void MoveToArchive_of_a_source_outside_its_declared_root_falls_back_to_a_flat_name()
    {
        // GetRelativePath would yield "..\..\x", which combined with the archive folder would write
        // OUTSIDE it. The fallback keeps the write contained.
        string sourceRoot = Path.Combine(_root, "src");
        Directory.CreateDirectory(sourceRoot);
        string outside = Path.Combine(_root, "elsewhere", "stray.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "original");
        string archive = Path.Combine(_root, "archive");
        (SourceDispositionService service, _) = Service();

        service.Dispose(ArchiveExecutionFor(outside, sourceRoot, archive, TargetLayout.PreserveStructure));

        string archived = Path.Combine(archive, "stray.txt");
        Assert.Equal("original", File.ReadAllText(archived));
        Assert.StartsWith(archive, Path.GetFullPath(archived), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoveToArchive_onto_an_occupied_name_fails_and_keeps_BOTH_files()
    {
        // Never silently destroy either the incoming original or the already-archived file.
        string sourceRoot = Path.Combine(_root, "src");
        string source = Path.Combine(sourceRoot, "photo.jpg");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(source, "incoming original");
        string archive = Path.Combine(_root, "archive");
        Directory.CreateDirectory(archive);
        string occupied = Path.Combine(archive, "photo.jpg");
        File.WriteAllText(occupied, "already archived");
        (SourceDispositionService service, _) = Service();

        var result = service.Dispose(ArchiveExecutionFor(source, sourceRoot, archive, TargetLayout.Flatten));

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.DispositionFailed, error!.Code);
        Assert.Equal("incoming original", File.ReadAllText(source));
        Assert.Equal("already archived", File.ReadAllText(occupied));
    }

    [Fact]
    public void MoveToArchive_with_no_archive_folder_fails_without_touching_the_source()
    {
        string sourceRoot = Path.Combine(_root, "src");
        string source = Path.Combine(sourceRoot, "photo.jpg");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(source, "original");
        (SourceDispositionService service, _) = Service();
        JobExecution execution = JobFixtures.Execution(
            source, sourceRoot, [Path.Combine(_root, "target", "f")], [Path.Combine(_root, "target")],
            JobFixtures.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: null));
        execution.Targets[0].State = TargetState.Placed;

        var result = service.Dispose(execution);

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.DispositionFailed, error!.Code);
        Assert.True(File.Exists(source), "a misconfigured archive must never cost the original");
    }

    /// <summary>The same misconfiguration, with the source already gone. The idempotency shortcut for a
    /// vanished source used to run BEFORE the archive guard and record a SUCCESSFUL MoveToArchive with a
    /// null destination — an audit record asserting the file had been archived to nowhere. The audit
    /// trail is the no-loss safety net, so it must report the misconfiguration instead of a completion.</summary>
    [Fact]
    public void MoveToArchive_with_no_archive_folder_still_fails_when_the_source_is_already_gone()
    {
        string sourceRoot = Path.Combine(_root, "src");
        Directory.CreateDirectory(sourceRoot);
        string source = Path.Combine(sourceRoot, "photo.jpg");
        File.WriteAllText(source, "original");
        (SourceDispositionService service, _) = Service();
        JobExecution execution = JobFixtures.Execution(
            source, sourceRoot, [Path.Combine(_root, "target", "f")], [Path.Combine(_root, "target")],
            JobFixtures.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: null));
        execution.Targets[0].State = TargetState.Placed;
        // Gone between job-committed and disposition — a user deleting it, or a second trigger's job
        // having disposed it already. The plan is built first because it snapshots the source's size.
        File.Delete(source);

        var result = service.Dispose(execution);

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.DispositionFailed, error!.Code);
        _audit.ReadRecent(10).TryGetValue(out IReadOnlyList<DispositionAuditRecord>? audited);
        Assert.Empty(audited!);   // no record at all beats a record that claims a completed archive
    }

    [Fact]
    public void A_skipped_conflict_downgrades_MoveToArchive_too()
    {
        string sourceRoot = Path.Combine(_root, "src");
        string source = Path.Combine(sourceRoot, "photo.jpg");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(source, "original");
        string archive = Path.Combine(_root, "archive");
        (SourceDispositionService service, _) = Service();
        JobExecution execution = ArchiveExecutionFor(source, sourceRoot, archive, TargetLayout.Flatten);
        execution.Targets[0].State = TargetState.SkippedConflict;

        var result = service.Dispose(execution);

        Assert.True(result.TryGetValue(out DispositionAuditRecord? record));
        Assert.Equal(OnSuccessAction.KeepSource, record!.Action);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(archive));
    }

    [Fact]
    public void An_unchanged_target_does_NOT_downgrade_disposition()
    {
        // SatisfiedUnchanged means the content is provably already at the target, so disposing the
        // source is safe — unlike SkippedConflict, where it was never delivered.
        string source = Path.Combine(_root, "unchanged.txt");
        File.WriteAllText(source, "x");
        (SourceDispositionService service, FakeTrashService trash) = Service();

        var result = service.Dispose(ExecutionFor(source, OnSuccessAction.MoveToTrash, targetState: TargetState.SatisfiedUnchanged));

        Assert.True(result.TryGetValue(out DispositionAuditRecord? record));
        Assert.Equal(OnSuccessAction.MoveToTrash, record!.Action);
        Assert.Contains(source, trash.Trashed);
    }

    [Fact]
    public void Recovery_variant_archives_flat_because_it_has_no_profile_layout()
    {
        // §7.3 row I: recovery lacks the profile, so archive falls back to a flat filename placement.
        // Documented and deliberate — pinned so it is not mistaken for a bug later.
        string sourceRoot = Path.Combine(_root, "src");
        string source = Path.Combine(sourceRoot, "2026", "07", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "original");
        string archive = Path.Combine(_root, "archive");
        (SourceDispositionService service, _) = Service();
        var snapshot = new SourceSnapshot { Path = source, SizeBytes = 8, LastWriteUtc = DateTimeOffset.UnixEpoch };

        var result = service.Dispose(
            Guid.NewGuid(), snapshot,
            JobFixtures.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: archive),
            anyTargetSkippedConflict: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("original", File.ReadAllText(Path.Combine(archive, "photo.jpg")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void Recovery_variant_treats_an_already_gone_source_as_disposed()
    {
        (SourceDispositionService service, _) = Service();
        var source = new SourceSnapshot { Path = Path.Combine(_root, "never-existed.txt"), SizeBytes = 0, LastWriteUtc = DateTimeOffset.UnixEpoch };

        var result = service.Dispose(Guid.NewGuid(), source, JobFixtures.Policy(onSuccess: OnSuccessAction.PermanentDelete), anyTargetSkippedConflict: false);
        Assert.True(result.IsSuccess);   // idempotent — no error though the file is absent
    }

    [Fact]
    public void A_permanent_delete_whose_audit_row_cannot_be_written_is_a_FAILED_disposition()
    {
        // The worst silent success in the product: the file is destroyed and the audit trail — the
        // spec's no-loss safety net — has no record of it. That must not report clean.
        string source = Path.Combine(_root, "gone-forever.txt");
        File.WriteAllText(source, "irreplaceable");
        var trash = new FakeTrashService(Path.Combine(_root, "bin"));
        var service = new SourceDispositionService(
            trash, new FailingAuditLog(), new FakeTimeProvider(), NullLogger<SourceDispositionService>.Instance);

        var result = service.Dispose(ExecutionFor(source, OnSuccessAction.PermanentDelete));

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.DispositionFailed, error!.Code);
        Assert.Contains("audit record could not be written", error.Message);
        Assert.False(File.Exists(source));   // the delete itself did happen — that is the whole problem
    }

    private sealed class FailingAuditLog : IDispositionAuditLog
    {
        public Result Append(DispositionAuditRecord record) => "the audit file is read-only";

        public Result<IReadOnlyList<DispositionAuditRecord>, string> ReadRecent(int count) =>
            Result<IReadOnlyList<DispositionAuditRecord>, string>.Success([]);
    }
}
