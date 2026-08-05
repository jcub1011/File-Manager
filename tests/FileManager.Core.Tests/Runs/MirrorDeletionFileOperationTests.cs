using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Journal;
using FileManager.Core.Runs;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Runs;

/// <summary>What a Mirror deletion pass actually does to the filesystem, asserted against the real
/// journal, the real audit log, and a real fake Recycle Bin.
/// <para>Every assertion checks BOTH halves of a deletion: that the destination is gone AND that the
/// file turned up in the bin. Spec §3.1.1 forbids hard-deleting an orphan, so "the file is no longer
/// at its destination" on its own is not the property that matters — it is satisfied equally by
/// recycling and by destroying.</para></summary>
public sealed class MirrorDeletionFileOperationTests
{
    [Fact]
    public async Task An_orphan_is_moved_to_the_recycle_bin_and_still_EXISTS_there()
    {
        using RunPlanHarness h = new("del-orphan");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        Assert.Equal(MirrorReconcileOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(orphan));
        // Recycled, not destroyed — the content is still retrievable.
        string recycled = Assert.Single(h.Bin());
        Assert.Equal("orphaned", File.ReadAllText(recycled));
    }

    [Fact]
    public async Task A_destination_file_a_source_writes_to_is_NEVER_deleted()
    {
        using RunPlanHarness h = new("del-survivor");
        h.WriteSource("shared.txt", "new");
        string survivor = h.WriteTarget("shared.txt", "old");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        Assert.Equal(1, result.Deleted);
        Assert.True(File.Exists(survivor));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task A_file_kept_by_ConflictResolution_Skip_is_NOT_deleted()
    {
        using RunPlanHarness h = new("del-skip");
        h.WriteSource("shared.txt", "incoming");
        string kept = h.WriteTarget("shared.txt", "existing");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(conflict: ConflictResolution.Skip));

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        // Skip still produces a destination operation for the prospective path, so the existing file
        // is a survivor. Deleting a file the conflict policy explicitly chose to keep would be absurd.
        Assert.Equal(MirrorReconcileOutcome.NothingToDo, result.Outcome);
        Assert.True(File.Exists(kept));
        Assert.Empty(h.Bin());
    }

    [Fact]
    public async Task A_second_identical_run_deletes_NOTHING_new()
    {
        using RunPlanHarness h = new("del-idempotent");
        h.WriteSource("kept.txt", "kept");
        h.WriteTarget("orphan.txt", "orphaned");
        (string first, _, _) = await h.PlanAsync(h.MirrorProfile());
        await h.Pass.DeleteAsync(h.Request(first));
        Assert.Single(h.Bin());

        // Re-plan against the now-clean destination and run again.
        (string second, _, _) = await h.PlanAsync(h.MirrorProfile());
        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(second));

        Assert.Equal(MirrorReconcileOutcome.NothingToDo, result.Outcome);
        Assert.Single(h.Bin());
    }

    [Fact]
    public async Task An_orphan_that_vanished_between_planning_and_deletion_is_skipped_not_a_failure()
    {
        using RunPlanHarness h = new("del-vanished");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        File.Delete(orphan);

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        // Idempotency by design: something else already removed it, which is the outcome we wanted.
        Assert.Equal(MirrorReconcileOutcome.PartiallyCompleted, result.Outcome);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task A_file_MODIFIED_after_the_run_was_planned_is_left_alone()
    {
        using RunPlanHarness h = new("del-modified");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        // The user approved removing the file as it was. This is a different file now.
        File.WriteAllText(orphan, "somebody wrote something important here");

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(orphan));
        Assert.Empty(h.Bin());
    }

    [Fact]
    public async Task A_path_this_run_actually_wrote_to_is_never_deleted_even_if_the_plan_named_it()
    {
        using RunPlanHarness h = new("del-selfwrite");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        // Forces the drift case: conflict resolution can legitimately land on a path the plan's probe
        // did not predict, and without this guard such a path reads as an orphan the run just created.
        MirrorDeletionResult result = await h.Pass.DeleteAsync(
            h.Request(dir, pathsWritten: new HashSet<string>([orphan], StringComparer.OrdinalIgnoreCase)));

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task A_path_another_job_HOLDS_the_lock_on_is_skipped_not_deleted()
    {
        using RunPlanHarness h = new("del-locked");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        h.UseConfig(h.DeletionConfig with { MirrorLockWaitTimeout = TimeSpan.FromMilliseconds(100) });
        Contracts.Primitives.Result<Core.Jobs.NormalizedPath, Core.Jobs.JobError> normalized =
            Core.Jobs.NormalizedPath.Create(orphan);
        normalized.TryGetValue(out Core.Jobs.NormalizedPath path);
        await using Core.Locking.PathLockSet held =
            await h.Locks.AcquireAsync([path], Core.Jobs.JobId.New());

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        // A live job owns the path. Waiting indefinitely would stall the pass; deleting under it would
        // race a placement. Skipping is the only safe answer.
        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task A_cancel_MID_ORPHAN_keeps_the_count_of_what_was_already_recycled()
    {
        using RunPlanHarness h = new("del-cancel-midorphan");
        h.WriteSource("kept.txt", "kept");
        for (int i = 0; i < 6; i++)
            h.WriteTarget($"orphan{i}.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        List<RunDeleteItem> orphans = RunPlanHarness.Deletes(dir);
        Assert.Equal(6, orphans.Count);

        // Hold the lock on the LAST orphan in plan order, with a lock wait long enough that the pass is
        // still parked on it when the cancel lands. The earlier five are recycled first.
        h.UseConfig(h.DeletionConfig with { MirrorLockWaitTimeout = TimeSpan.FromSeconds(30) });
        Core.Jobs.NormalizedPath.Create(orphans[^1].Path).TryGetValue(out Core.Jobs.NormalizedPath path);
        await using Core.Locking.PathLockSet held =
            await h.Locks.AcquireAsync([path], Core.Jobs.JobId.New());

        using CancellationTokenSource cancel = new();
        Task<MirrorDeletionResult> pass = h.Pass.DeleteAsync(h.Request(dir), cancel.Token);
        for (int waited = 0; h.Bin().Count < 5 && waited < 10_000; waited += 20)
            await Task.Delay(20);
        Assert.Equal(5, h.Bin().Count);
        await cancel.CancelAsync();
        MirrorDeletionResult result = await pass;

        // The cancel is raised INSIDE the orphan, from the lock await. It used to unwind past the loop's
        // counters into DeleteAsync's catch, which reported Deleted = 0 — so the user was told nothing had
        // been removed while five of their destination files sat in the Recycle Bin, and the journal's own
        // close record contradicted its five mrdel/mrdone pairs.
        Assert.Equal(MirrorReconcileOutcome.AbortedMidPass, result.Outcome);
        Assert.Equal(5, result.Deleted);
        Assert.Equal(5, h.Bin().Count);
        Assert.NotNull(result.AbortReason);

        // The durable account agrees with the result, which is the whole point of writing one.
        MirrorReconcileClosedRecord closed = Assert.Single(
            h.JournalRecords().OfType<MirrorReconcileClosedRecord>());
        Assert.Equal(5, closed.Deleted);
        Assert.Equal(MirrorReconcileOutcome.AbortedMidPass, closed.Outcome);
        Assert.Equal(
            h.JournalRecords().OfType<MirrorOrphanTrashedRecord>().Count(r => r.Error is null),
            closed.Deleted);
    }

    // ---- write-ahead ordering and the audit trail ------------------------------------------------

    [Fact]
    public async Task Every_deletion_writes_its_trashing_record_BEFORE_its_trashed_record()
    {
        using RunPlanHarness h = new("del-writeahead");
        h.WriteSource("kept.txt", "kept");
        h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        await h.Pass.DeleteAsync(h.Request(dir));

        List<JournalRecord> records = [.. h.JournalRecords()];
        int opened = records.FindIndex(r => r is MirrorReconcileOpenedRecord);
        int trashing = records.FindIndex(r => r is MirrorOrphanTrashingRecord);
        int trashed = records.FindIndex(r => r is MirrorOrphanTrashedRecord);
        int closed = records.FindIndex(r => r is MirrorReconcileClosedRecord);
        Assert.True(opened >= 0 && trashing > opened, "the pass must be opened before it removes anything");
        // The intent must reach disk before the destructive act, so a crash in between still leaves the
        // path on the record.
        Assert.True(trashed > trashing, "the write-ahead record must precede its completion record");
        Assert.True(closed > trashed, "the pass must close after its deletions");
    }

    [Fact]
    public async Task Every_deletion_writes_exactly_one_audit_row_naming_the_recycle_bin()
    {
        using RunPlanHarness h = new("del-audit");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        MirrorDeletionRequest request = h.Request(dir);

        await h.Pass.DeleteAsync(request);

        MirrorDeletionAuditRecord row = Assert.Single(h.AuditRows());
        Assert.Equal(orphan, row.DestinationPath);
        Assert.Equal(h.TargetDir, row.TargetRoot);
        Assert.Equal(request.RunId, row.RunId);
        Assert.Equal(request.PassId.Value, row.PassId);
        Assert.Equal(MirrorDeletionAuditLog.RecycleBinDestination, row.Destination);
        Assert.Equal(8, row.SizeBytes);
    }

    [Fact]
    public async Task A_skipped_orphan_writes_NO_audit_row()
    {
        using RunPlanHarness h = new("del-audit-skip");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        File.WriteAllText(orphan, "changed");

        await h.Pass.DeleteAsync(h.Request(dir));

        // The trail records deletions that happened. A row for a file still sitting on disk would make
        // it useless as the no-loss safety net.
        Assert.Empty(h.AuditRows());
    }

    [Fact]
    public async Task The_pass_narrates_itself_into_a_readable_per_pass_log()
    {
        using RunPlanHarness h = new("del-log");
        h.WriteSource("kept.txt", "kept");
        h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        MirrorDeletionRequest request = h.Request(dir);

        await h.Pass.DeleteAsync(request);

        h.JobLog.Read(request.PassId.Value).TryGetValue(out IReadOnlyList<string>? lines);
        Assert.NotNull(lines);
        Assert.Contains(lines, l => l.Contains("opened", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("deleted", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("closed Completed", StringComparison.Ordinal));
    }

    // ---- documented limitation -------------------------------------------------------------------

    [Fact]
    public async Task Empty_directories_left_behind_are_NOT_removed()
    {
        using RunPlanHarness h = new("del-empty-dirs");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget(Path.Combine("gone", "orphan.txt"), "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        await h.Pass.DeleteAsync(h.Request(dir));

        // DOCUMENTED LIMITATION, pinned so it reads as a decision rather than a bug: the sweep yields
        // files only, so a mirrored destination accumulates an empty directory skeleton where a source
        // subtree used to be.
        Assert.False(File.Exists(orphan));
        Assert.True(Directory.Exists(Path.GetDirectoryName(orphan)));
    }
}
