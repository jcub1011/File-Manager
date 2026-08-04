using FileManager.Contracts.Profiles;
using FileManager.Core.Journal;
using FileManager.Core.Runs;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Runs;

/// <summary>The fail-closed gates: every reason a Mirror deletion pass must remove NOTHING.
///
/// <para>Each test asserts the same three things — the orphan is still on disk, the Recycle Bin is
/// empty, and the result carries a reason a user could act on. That uniformity is the point: these are
/// the cases where the engine has partial information, and the whole design rests on partial
/// information never being enough to delete. A gate that "mostly" holds is not a gate.</para></summary>
public sealed class MirrorDeletionGateTests
{
    /// <summary>Plans a Mirror run with exactly one orphan and returns everything a gate test needs.</summary>
    private static async Task<(RunPlanHarness H, string Dir, string Orphan)> OneOrphan(string label)
    {
        RunPlanHarness h = new(label);
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        Assert.Single(RunPlanHarness.Deletes(dir));   // the gate is what stops it, not an empty plan
        return (h, dir, orphan);
    }

    private static void AssertNothingDeleted(RunPlanHarness h, string orphan, MirrorDeletionResult result)
    {
        Assert.Equal(MirrorReconcileOutcome.AbortedBeforeDeleting, result.Outcome);
        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(orphan), "the orphan must still be at its destination");
        Assert.Empty(h.Bin());
        Assert.False(string.IsNullOrWhiteSpace(result.AbortReason), "an abort must explain itself to the user");
        // A refusal writes no journal records at all, which is what keeps "refused" distinguishable on
        // disk from "started, then stopped".
        Assert.DoesNotContain(h.JournalRecords(), r => r is MirrorReconcileOpenedRecord);
        Assert.Empty(h.AuditRows());
    }

    // ---- plan completeness -----------------------------------------------------------------------

    [Fact]
    public async Task A_TRUNCATED_plan_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-truncated");
        using (h)
        {
            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, planTruncated: true));

            // The survivor set is a prefix, so a file that appears to have no source may well be
            // written by a source the scan never reached.
            AssertNothingDeleted(h, orphan, result);
            Assert.Contains("incomplete", result.AbortReason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_INCOMPLETE_enumeration_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-enum");
        using (h)
        {
            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, enumerationIncomplete: true));

            // Deliberately stricter than the dry run, which merely marks such a report truncated: real,
            // present source files sit behind an unreadable subdirectory and are absent from the
            // survivor set. A preview may be incomplete; a deletion may not.
            AssertNothingDeleted(h, orphan, result);
        }
    }

    // ---- the run's own health --------------------------------------------------------------------

    [Fact]
    public async Task A_FAILED_copy_job_in_the_run_suppresses_every_deletion()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-failedcopy");
        using (h)
        {
            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, copyJobsFailed: 1));

            // Until every copy has landed the destination is not a mirror of the source, and removing
            // the old copy of a file whose replacement never arrived is data loss.
            AssertNothingDeleted(h, orphan, result);
            Assert.Contains("could not be copied", result.AbortReason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_PAUSED_engine_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-paused");
        using (h)
        {
            h.Pause.SetPaused(true);

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

            AssertNothingDeleted(h, orphan, result);
        }
    }

    // ---- scope ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_SCOPED_run_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-scoped");
        using (h)
        {
            MirrorDeletionResult result = await h.Pass.DeleteAsync(
                h.Request(dir, scopePath: Path.Combine(h.SourceDir, "sub")));

            // A narrowed plan covers only part of the source set, so everything outside the scope reads
            // as an orphan. This is the documented limitation that a Mirror profile only reconciles
            // from a whole-profile run.
            AssertNothingDeleted(h, orphan, result);
            Assert.Contains("not the whole profile", result.AbortReason!, StringComparison.Ordinal);
        }
    }

    // ---- profile shape ---------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_with_NO_SOURCES_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-nosources");
        using (h)
        {
            Profile stripped = RunPlanHarness.Header(dir).Profile with { Sources = [] };

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, profile: stripped));

            // Zero sources means EVERY destination file is an orphan — an implicit "empty the target
            // tree" that must never be reachable by omission.
            AssertNothingDeleted(h, orphan, result);
            Assert.Contains("no sources", result.AbortReason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_profile_with_NO_TARGETS_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-notargets");
        using (h)
        {
            Profile stripped = RunPlanHarness.Header(dir).Profile with { Targets = [] };

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, profile: stripped));

            AssertNothingDeleted(h, orphan, result);
        }
    }

    [Fact]
    public async Task An_ADDITIVE_ARCHIVE_profile_deletes_NOTHING_even_if_asked()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-additive");
        using (h)
        {
            Profile additive = RunPlanHarness.Header(dir).Profile with { SyncMode = SyncMode.AdditiveArchive };

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, profile: additive));

            // Belt and braces: nothing should ever hand this pass an additive profile, but "nothing at
            // a Target is ever removed" is the mode's defining promise and deserves its own gate.
            AssertNothingDeleted(h, orphan, result);
        }
    }

    [Fact]
    public async Task A_MISSING_source_root_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-missingsource");
        using (h)
        {
            Profile moved = RunPlanHarness.Header(dir).Profile with
            {
                Sources = [new SourceConfig { Path = Path.Combine(h.Root, "not-there") }],
            };

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir, profile: moved));

            // The explicit per-source existence check matters because a source root the walk never
            // reached raises no enumeration fault of its own. An unmounted share must not read as "the
            // source is empty, delete everything".
            AssertNothingDeleted(h, orphan, result);
            Assert.Contains("missing or unreadable", result.AbortReason!, StringComparison.Ordinal);
        }
    }

    // ---- volume guards ---------------------------------------------------------------------------

    [Fact]
    public async Task Exceeding_the_orphan_COUNT_cap_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-cap");
        using (h)
        {
            h.UseConfig(h.DeletionConfig with { MirrorMaxOrphans = 0 });

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

            // A cap that removes the first N and abandons the rest is worse than one that refuses and
            // explains.
            AssertNothingDeleted(h, orphan, result);
            Assert.Contains("safety limit", result.AbortReason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_RATIO_guard_deletes_NOTHING_when_most_of_a_target_root_would_go()
    {
        using RunPlanHarness h = new("gate-ratio");
        h.WriteSource("kept.txt", "kept");
        for (int i = 0; i < 25; i++)
            h.WriteTarget($"orphan{i}.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        Assert.Equal(25, RunPlanHarness.Deletes(dir).Count);

        // 25 orphans out of 26 files swept under the root: this is what a TargetLayout flip or a source
        // share that remounted empty looks like, and every other gate reports green for it. Only the
        // shape of the result is suspicious, so only a proportion test can catch it.
        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(
            dir, sweptByRoot: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [h.TargetDir] = 26 }));

        Assert.Equal(MirrorReconcileOutcome.AbortedBeforeDeleting, result.Outcome);
        Assert.Empty(h.Bin());
        Assert.Equal(25, Directory.GetFiles(h.TargetDir).Length);
        Assert.Contains("safety limit", result.AbortReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ratio_guard_does_not_fire_below_its_floor()
    {
        using RunPlanHarness h = new("gate-ratio-floor");
        h.WriteSource("kept.txt", "kept");
        h.WriteTarget("orphan1.txt", "one");
        h.WriteTarget("orphan2.txt", "two");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        // 2 of 3 files under the root is 67%, way over the fraction — but a root holding a handful of
        // files that legitimately lose most of them is entirely normal, which is what the floor is for.
        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(
            dir, sweptByRoot: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [h.TargetDir] = 3 }));

        Assert.Equal(MirrorReconcileOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Deleted);
    }

    // ---- failures during the pass ----------------------------------------------------------------

    [Fact]
    public async Task A_failed_WRITE_AHEAD_append_leaves_the_orphan_in_PLACE()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-journalfail");
        using (h)
        {
            h.Journal.FailOnRecordType = typeof(MirrorOrphanTrashingRecord);

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

            // The write-ahead record is the precondition for removing anything: no record, no deletion.
            Assert.Equal(0, result.Deleted);
            Assert.True(File.Exists(orphan));
            Assert.Empty(h.Bin());
        }
    }

    [Fact]
    public async Task Repeated_write_ahead_failures_abort_the_pass_MID_way()
    {
        using RunPlanHarness h = new("gate-journalfail-abort");
        h.WriteSource("kept.txt", "kept");
        for (int i = 0; i < 6; i++)
            h.WriteTarget($"orphan{i}.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        h.Journal.FailOnRecordType = typeof(MirrorOrphanTrashingRecord);
        h.UseConfig(h.DeletionConfig with { MirrorMaxConsecutiveJournalFailures = 2 });

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        // Past a few consecutive failures the journal is effectively gone, and continuing would remove
        // files with no durable record that we did.
        Assert.Equal(MirrorReconcileOutcome.AbortedMidPass, result.Outcome);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(6, Directory.GetFiles(h.TargetDir).Length);
        Assert.Contains("journal", result.AbortReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_trash_move_is_reported_and_the_pass_ends_PartiallyCompleted()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-trashfail");
        using (h)
        {
            h.Trash.FailOnPaths.Add(orphan);

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

            Assert.Equal(MirrorReconcileOutcome.PartiallyCompleted, result.Outcome);
            Assert.Equal(0, result.Deleted);
            Assert.True(File.Exists(orphan));
            MirrorDeletionFailure failure = Assert.Single(result.Failures);
            Assert.Equal(orphan, failure.Path);
            // The completion record must carry the error, so the journal shows the attempt and its
            // outcome rather than only the intent.
            Assert.Contains(
                h.JournalRecords(), r => r is MirrorOrphanTrashedRecord { Error: not null });
        }
    }

    [Fact]
    public async Task A_failed_AUDIT_append_is_reported_as_a_FAILED_deletion_even_though_the_file_is_gone()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-auditfail");
        using (h)
        {
            h.Audit.FailOnPaths.Add(orphan);

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

            // The file IS in the bin and cannot be un-deleted, but the no-loss trail could not be
            // written. Reporting that as a clean success is exactly how an audit trail quietly stops
            // being trustworthy — so it counts as a failure despite the move having worked.
            Assert.Equal(MirrorReconcileOutcome.PartiallyCompleted, result.Outcome);
            Assert.Equal(0, result.Deleted);
            Assert.False(File.Exists(orphan));
            Assert.Single(h.Bin());
            MirrorDeletionFailure failure = Assert.Single(result.Failures);
            Assert.Contains("audit", failure.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Pausing_MID_pass_stops_at_a_path_boundary()
    {
        using RunPlanHarness h = new("gate-pause-mid");
        h.WriteSource("kept.txt", "kept");
        for (int i = 0; i < 5; i++)
            h.WriteTarget($"orphan{i}.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        // Pause as soon as the first orphan is recycled: the user pressed pause, and the next path
        // boundary is the soonest that can be honoured without abandoning a half-done move.
        h.Trash.OnMove = _ => h.Pause.SetPaused(true);

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        Assert.Equal(MirrorReconcileOutcome.AbortedMidPass, result.Outcome);
        Assert.Equal(1, result.Deleted);
        Assert.Single(h.Bin());
        Assert.Equal(4, Directory.GetFiles(h.TargetDir).Length);
    }

    [Fact]
    public async Task Cancellation_MID_pass_stops_and_leaves_the_rest()
    {
        using RunPlanHarness h = new("gate-cancel-mid");
        h.WriteSource("kept.txt", "kept");
        for (int i = 0; i < 5; i++)
            h.WriteTarget($"orphan{i}.txt", "orphaned");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        using CancellationTokenSource cts = new();
        h.Trash.OnMove = _ => cts.Cancel();

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir), cts.Token);

        // Whatever reached the bin stays there — re-deleting it or restoring it would both be wrong.
        Assert.Equal(MirrorReconcileOutcome.AbortedMidPass, result.Outcome);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(4, Directory.GetFiles(h.TargetDir).Length);
    }

    [Fact]
    public async Task A_failed_OPENED_append_deletes_NOTHING()
    {
        (RunPlanHarness h, string dir, string orphan) = await OneOrphan("gate-openfail");
        using (h)
        {
            h.Journal.FailOnRecordType = typeof(MirrorReconcileOpenedRecord);

            MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

            AssertNothingDeleted(h, orphan, result);
        }
    }

    [Fact]
    public async Task An_empty_orphan_list_is_NothingToDo_and_writes_no_records()
    {
        using RunPlanHarness h = new("gate-nothing");
        h.WriteSource("kept.txt", "kept");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        MirrorDeletionResult result = await h.Pass.DeleteAsync(h.Request(dir));

        Assert.Equal(MirrorReconcileOutcome.NothingToDo, result.Outcome);
        Assert.DoesNotContain(h.JournalRecords(), r => r is MirrorReconcileRecord);
    }
}
