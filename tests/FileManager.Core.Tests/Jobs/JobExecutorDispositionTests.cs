using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>What happens to the user's ORIGINAL file. Three of the four <c>OnSuccess</c> actions move
/// or destroy it, so these tests are the last line of defence against data loss.
/// <para>The positive cases assert the source reached exactly the right place; the negative cases
/// assert invariant <b>I-DISPOSE</b> — the source is never disposed unless <c>job-committed</c> was
/// journaled, which requires every target to be Placed or SatisfiedUnchanged. Every failure and skip
/// path is covered, because each one is a way a user could lose a file that was never copied.</para></summary>
public sealed class JobExecutorDispositionTests
{
    private static Profile ProfileWithLayout(JobExecutorHarness h, TargetLayout layout) =>
        TestProfiles.Valid(h.SourceDir, h.TargetDir) with { TargetLayout = layout };

    // ---- the four OnSuccess actions on a committed job --------------------------------------------

    [Fact]
    public async Task KeepSource_leaves_the_original_exactly_where_it_was()
    {
        using JobExecutorHarness h = new("disp-keep");
        string source = h.WriteSource("keep.txt", "original");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, h.TargetPath("keep.txt"), h.Policy(onSuccess: OnSuccessAction.KeepSource)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.True(File.Exists(source));
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Empty(h.Trash.Trashed);
    }

    [Fact]
    public async Task MoveToArchive_flattened_moves_the_original_into_the_archive_root()
    {
        using JobExecutorHarness h = new("disp-archive-flat");
        string source = h.WriteSource(Path.Combine("nested", "photo.jpg"), "original bytes");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(
            source, h.TargetPath("photo.jpg"),
            h.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: h.ArchiveDir),
            profile: ProfileWithLayout(h, TargetLayout.Flatten)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.False(File.Exists(source), "the original must have moved out of the source tree");
        string archived = Path.Combine(h.ArchiveDir, "photo.jpg");
        Assert.True(File.Exists(archived), "the original must be in the archive, not gone");
        Assert.Equal("original bytes", File.ReadAllText(archived));
        Assert.Equal("original bytes", File.ReadAllText(h.TargetPath("photo.jpg")));
    }

    [Fact]
    public async Task MoveToArchive_preserving_structure_mirrors_the_source_relative_path()
    {
        using JobExecutorHarness h = new("disp-archive-nested");
        string relative = Path.Combine("2026", "07", "photo.jpg");
        string source = h.WriteSource(relative, "original bytes");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(
            source, h.TargetPath("photo.jpg"),
            h.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: h.ArchiveDir),
            profile: ProfileWithLayout(h, TargetLayout.PreserveStructure)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.False(File.Exists(source));
        string archived = Path.Combine(h.ArchiveDir, relative);
        Assert.True(File.Exists(archived), $"expected the original mirrored at {archived}");
        Assert.Equal("original bytes", File.ReadAllText(archived));
    }

    [Fact]
    public async Task MoveToArchive_onto_an_occupied_name_fails_the_disposition_and_KEEPS_the_original()
    {
        // The single most dangerous branch: archiving must never silently destroy the original when
        // the destination name is taken. File.Move(overwrite: false) throws, and the job must report
        // the disposition error while leaving BOTH files intact.
        using JobExecutorHarness h = new("disp-archive-collision");
        string source = h.WriteSource("photo.jpg", "incoming original");
        Directory.CreateDirectory(h.ArchiveDir);
        string occupied = Path.Combine(h.ArchiveDir, "photo.jpg");
        File.WriteAllText(occupied, "a previously archived file");

        JobPlan plan = h.Plan(
            source, h.TargetPath("photo.jpg"),
            h.Policy(onSuccess: OnSuccessAction.MoveToArchive, archiveFolder: h.ArchiveDir),
            profile: ProfileWithLayout(h, TargetLayout.Flatten));
        JobCompletion completion = await h.Executor.ExecuteAsync(plan);

        // The copy is safe, so the job still succeeds — but nothing was destroyed.
        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("incoming original", File.ReadAllText(source));
        Assert.Equal("a previously archived file", File.ReadAllText(occupied));
        Assert.Equal("incoming original", File.ReadAllText(h.TargetPath("photo.jpg")));

        // The failure is recorded where an operator will find it.
        JobClosedRecord closed = Assert.Single(h.JournalRecords().OfType<JobClosedRecord>());
        Assert.NotNull(closed.DispositionError);
        Assert.Contains("disposition error", string.Join("\n", h.JobLogLines(plan.JobId)));
    }

    [Fact]
    public async Task MoveToTrash_recycles_the_original_after_the_commit()
    {
        using JobExecutorHarness h = new("disp-trash");
        string source = h.WriteSource("bin-me.txt", "original");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, h.TargetPath("bin-me.txt"), h.Policy(onSuccess: OnSuccessAction.MoveToTrash)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.False(File.Exists(source));
        Assert.Contains(source, h.Trash.Trashed);
        // Recycled, not destroyed: the content is recoverable from the bin.
        Assert.Equal("original", File.ReadAllText(Path.Combine(h.TrashBin, "bin-me.txt")));
        Assert.Equal("original", File.ReadAllText(h.TargetPath("bin-me.txt")));
    }

    [Fact]
    public async Task PermanentDelete_removes_the_original_only_after_the_copy_is_verified_in_place()
    {
        using JobExecutorHarness h = new("disp-delete");
        string source = h.WriteSource("gone.txt", "original");
        string final = h.TargetPath("gone.txt");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(onSuccess: OnSuccessAction.PermanentDelete)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.False(File.Exists(source));
        Assert.Equal("original", File.ReadAllText(final));   // the content survives at the target
    }

    [Theory]
    [InlineData(OnSuccessAction.MoveToArchive)]
    [InlineData(OnSuccessAction.MoveToTrash)]
    [InlineData(OnSuccessAction.PermanentDelete)]
    public async Task A_disposing_action_writes_an_audit_record(OnSuccessAction action)
    {
        // The deletion audit trail is the safety net for the no-loss goal (spec §7), so every
        // disposition that actually touches the original must be recorded.
        using JobExecutorHarness h = new("disp-audit");
        string source = h.WriteSource("audited.txt", "original");

        await h.Executor.ExecuteAsync(h.Plan(
            source, h.TargetPath("audited.txt"),
            h.Policy(onSuccess: action, archiveFolder: h.ArchiveDir),
            profile: ProfileWithLayout(h, TargetLayout.Flatten)));

        h.Audit.ReadRecent(10).TryGetValue(out IReadOnlyList<DispositionAuditRecord>? records);
        Assert.NotNull(records);
        Assert.Contains(records!, r => r.Action == action && r.SourcePath == source);
    }

    // ---- I-DISPOSE: no commit → no disposition ----------------------------------------------------

    [Theory]
    [InlineData(OnSuccessAction.MoveToArchive)]
    [InlineData(OnSuccessAction.MoveToTrash)]
    [InlineData(OnSuccessAction.PermanentDelete)]
    public async Task A_filtered_job_never_disposes_the_source(OnSuccessAction action)
    {
        using JobExecutorHarness h = new("disp-filtered");
        string source = h.WriteSource("scratch.tmp", "original");
        Profile profile = TestProfiles.Valid(h.SourceDir, h.TargetDir) with
        {
            TargetLayout = TargetLayout.Flatten,
            Filters = new FilterSet { ExcludeGlob = ["*.tmp"] },
        };

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(
            source, h.TargetPath("scratch.tmp"),
            h.Policy(onSuccess: action, archiveFolder: h.ArchiveDir), profile: profile));

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.Filtered, completion.SkipReason);
        Assert.True(File.Exists(source), "a file that was never copied must never be disposed");
        Assert.Empty(h.Trash.Trashed);
        Assert.False(Directory.Exists(h.ArchiveDir) && Directory.GetFiles(h.ArchiveDir).Length > 0);
    }

    [Theory]
    [InlineData(OnSuccessAction.MoveToTrash)]
    [InlineData(OnSuccessAction.PermanentDelete)]
    public async Task A_preflight_failure_never_disposes_the_source(OnSuccessAction action)
    {
        using JobExecutorHarness h = new("disp-preflight");
        string source = h.WriteSource("big.txt", "original");
        h.Volumes.Free = 1;   // not enough for the copy

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, h.TargetPath("big.txt"), h.Policy(onSuccess: action)));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.True(File.Exists(source));
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Empty(h.Trash.Trashed);
    }

    [Theory]
    [InlineData(OnSuccessAction.MoveToTrash)]
    [InlineData(OnSuccessAction.PermanentDelete)]
    public async Task A_placement_failure_never_disposes_the_source(OnSuccessAction action)
    {
        using JobExecutorHarness h = new("disp-placefail");
        string source = h.WriteSource("doomed.txt", "original");
        h.Metadata.FailApply = true;   // fails after the temp is written, forcing rollback

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(
            source, h.TargetPath("doomed.txt"),
            h.Policy(onSuccess: action, metadataOnConflict: MetadataOnConflict.FailJob)));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.True(File.Exists(source), "a failed job must leave the original in place");
        Assert.Equal("original", File.ReadAllText(source));
        Assert.Empty(h.Trash.Trashed);
        Assert.DoesNotContain(h.JournalRecords(), r => r is JobCommittedRecord);
    }

    [Fact]
    public async Task A_verification_mismatch_never_disposes_the_source()
    {
        // Simulated corruption on the read-back at the target: the bytes that landed are not the bytes
        // that were sealed, so the copy is untrustworthy and the original must survive.
        using JobExecutorHarness h = new("disp-corrupt");
        string source = h.WriteSource("corrupt.txt", "original");
        string final = h.TargetPath("corrupt.txt");
        // Corrupt only the read-back of the temp at the target, not the source seal.
        h.Hasher.CorruptPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(onSuccess: OnSuccessAction.PermanentDelete)));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.Equal(JobErrorCode.VerificationMismatch, completion.Error?.Code);
        Assert.True(File.Exists(source), "corruption must never cost the user the original");
        Assert.False(File.Exists(final), "a target that failed verification must not be left in place");
        Assert.DoesNotContain(h.JournalRecords(), r => r is JobCommittedRecord);
    }

    [Fact]
    public async Task A_skipped_conflict_downgrades_disposition_and_keeps_the_source()
    {
        // The source was not delivered to this target, so disposing it would violate the spirit of
        // I-DISPOSE even though the job "succeeded".
        using JobExecutorHarness h = new("disp-conflict");
        string source = h.WriteSource("doc.txt", "incoming");
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), "existing wins");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(
            source, final,
            h.Policy(conflict: ConflictResolution.Skip, onSuccess: OnSuccessAction.PermanentDelete)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.True(File.Exists(source), "a source kept out by a conflict must not be deleted");
        Assert.Equal("existing wins", File.ReadAllText(final));
    }

    [Fact]
    public async Task A_partially_delivered_multi_target_job_does_not_dispose_the_source()
    {
        // One target takes the file, the other keeps its own — the source is not fully delivered, so
        // the disposing action must downgrade.
        using JobExecutorHarness h = new("disp-partial");
        string source = h.WriteSource("doc.txt", "incoming");
        string[] roots = [Path.Combine(h.Root, "t1"), Path.Combine(h.Root, "t2")];
        string freeTarget = Path.Combine(roots[0], "doc.txt");
        string occupiedTarget = Path.Combine(roots[1], "doc.txt");
        h.WriteExistingTarget(occupiedTarget, "existing wins");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(
            source, [freeTarget, occupiedTarget],
            h.Policy(conflict: ConflictResolution.Skip, onSuccess: OnSuccessAction.PermanentDelete),
            targetRoots: roots));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.True(File.Exists(source));
        Assert.Equal("incoming", File.ReadAllText(freeTarget));
        Assert.Equal("existing wins", File.ReadAllText(occupiedTarget));
    }

    [Fact]
    public async Task An_unchanged_at_all_targets_job_neither_commits_nor_disposes()
    {
        // Re-delivery of an already-satisfied file closes Skipped without a commit record. Since
        // job-committed alone authorizes disposition, the source must stay — otherwise a repeated
        // trigger under PermanentDelete would silently eat the source on the second delivery.
        using JobExecutorHarness h = new("disp-unchanged");
        string source = h.WriteSource("doc.txt", "stable");
        string final = h.TargetPath("doc.txt");
        await h.Executor.ExecuteAsync(h.Plan(source, final, h.Policy(onSuccess: OnSuccessAction.KeepSource)));
        Assert.True(File.Exists(source));

        JobCompletion repeat = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(onSuccess: OnSuccessAction.PermanentDelete)));

        Assert.Equal(JobOutcome.Skipped, repeat.Outcome);
        Assert.Equal(SkipReason.UnchangedAtAllTargets, repeat.SkipReason);
        Assert.True(File.Exists(source), "an unchanged re-delivery must not dispose the source");
        Assert.Equal("stable", File.ReadAllText(final));
    }

    [Fact]
    public async Task A_source_that_vanished_under_the_lock_is_skipped_with_no_journal_trace()
    {
        // Overlap safety (spec §12): a second profile already disposed this source. The later job must
        // end SKIPPED(SourceDisposed) with no side effects — not an error, and no partial target.
        using JobExecutorHarness h = new("disp-overlap");
        string source = h.WriteSource("raced.txt", "original");
        JobPlan plan = h.Plan(source, h.TargetPath("raced.txt"), h.Policy(onSuccess: OnSuccessAction.PermanentDelete));
        File.Delete(source);   // the other job disposed it between planning and execution

        JobCompletion completion = await h.Executor.ExecuteAsync(plan);

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.SourceDisposed, completion.SkipReason);
        Assert.Empty(h.JournalRecords());
        Assert.Empty(Directory.GetFiles(h.TargetDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Disposition_happens_after_the_commit_record_not_before()
    {
        // Ordering, not just outcome: job-committed must be journaled before the original is touched.
        using JobExecutorHarness h = new("disp-order");
        string source = h.WriteSource("ordered.txt", "original");
        JobPlan plan = h.Plan(source, h.TargetPath("ordered.txt"), h.Policy(onSuccess: OnSuccessAction.PermanentDelete));

        await h.Executor.ExecuteAsync(plan);

        List<string> log = [.. h.JobLogLines(plan.JobId)];
        int committed = log.FindIndex(l => l.Contains("committed", StringComparison.Ordinal));
        int succeeded = log.FindIndex(l => l.Contains("succeeded", StringComparison.Ordinal));
        Assert.True(committed >= 0 && succeeded > committed,
            $"disposition must follow the commit; log was:{Environment.NewLine}{string.Join(Environment.NewLine, log)}");
        Assert.False(File.Exists(source));
    }
}
