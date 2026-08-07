using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Runs;

/// <summary>The frozen work list a run executes from, asserted against the real planning pipeline over
/// a real temp filesystem.
/// <para>This is the load-bearing test file for the whole run design: if the snapshot does not name
/// exactly the files the preview named, then a live Mirror run deletes something the user never
/// approved. Every assertion here is about which items land in the snapshot and why.</para></summary>
public sealed class RunSnapshotTests
{
    // ---- copy items ------------------------------------------------------------------------------

    [Fact]
    public async Task Every_source_file_becomes_exactly_one_copy_item()
    {
        using RunPlanHarness h = new("snap-copies");
        h.WriteSource("a.txt", "aaa");
        h.WriteSource("nested/b.txt", "bb");

        (string dir, _, Result completion) = await h.PlanAsync(h.AdditiveProfile());

        Assert.False(completion.TryGetError(out string? error), error);
        List<RunCopyItem> copies = RunPlanHarness.Copies(dir);
        Assert.Equal(2, copies.Count);
        Assert.Contains(copies, c => c.SourcePath.EndsWith("a.txt", StringComparison.Ordinal) && c.SizeBytes == 3);
        Assert.Contains(copies, c => c.SourcePath.EndsWith("b.txt", StringComparison.Ordinal) && c.SizeBytes == 2);
        // Every copy item must name the configured Source root, because that is what the destination
        // path is resolved relative to and what the M:1 priority rank is derived from.
        Assert.All(copies, c => Assert.Equal(h.SourceDir, c.SourceRoot));
        Assert.All(copies, c => Assert.Equal(0, c.SourceIndex));
    }

    [Fact]
    public async Task The_header_totals_match_the_items_actually_written()
    {
        using RunPlanHarness h = new("snap-totals");
        h.WriteSource("a.txt", "12345");
        h.WriteSource("b.txt", "678");
        h.WriteTarget("orphan.txt", "orphaned");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(RunPlanHarness.Copies(dir).Count, header.CopyItemCount);
        Assert.Equal(RunPlanHarness.Deletes(dir).Count, header.DeleteItemCount);
        // The counts are the run's progress denominator and the barrier's target, so a drift between
        // header and items would desynchronize both.
        Assert.Equal(2, header.CopyItemCount);
        Assert.Equal(8, header.CopyBytes);
        Assert.Equal(1, header.DeleteItemCount);
        Assert.Equal(8, header.DeleteBytes);
    }

    [Fact]
    public async Task A_filter_EXCLUDED_source_file_produces_no_copy_item()
    {
        using RunPlanHarness h = new("snap-filtered");
        h.WriteSource("keep.txt", "keep");
        h.WriteSource("drop.tmp", "drop");

        (string dir, _, _) = await h.PlanAsync(
            h.AdditiveProfile() with { Filters = new FilterSet { ExcludeGlob = ["*.tmp"] } });

        RunCopyItem only = Assert.Single(RunPlanHarness.Copies(dir));
        Assert.EndsWith("keep.txt", only.SourcePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_file_already_identical_at_the_target_produces_no_copy_item()
    {
        using RunPlanHarness h = new("snap-unchanged");
        h.WriteSource("same.txt", "identical");
        h.WriteTarget("same.txt", "identical");

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile());

        // The plan says there is nothing to do for this file, so the run does nothing for it — the
        // snapshot IS the decision. Re-copying it "just in case" would mean the executed run and the
        // approved preview disagreed, which is the one thing this design exists to prevent.
        Assert.Empty(RunPlanHarness.Copies(dir));
    }

    // ---- the display halves (source and destination projections) ---------------------------------
    // Display only: nothing in execution reads these, and they hold WIDER sets than the executable halves.
    // copies.ndjsonl lists only files there is work for and deletes.ndjsonl only orphans, so a view built
    // from those two shows an empty panel wherever a profile is already up to date or removes nothing —
    // which reads as "the preview found nothing".

    [Fact]
    public async Task Every_scanned_source_gets_a_source_item_keyed_by_its_plan_ordinal()
    {
        using RunPlanHarness h = new("snap-source-ordinals");
        h.WriteSource("a.txt", "aaa");
        h.WriteSource("nested/b.txt", "bb");

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile());

        List<RunSourceItem> sources = RunPlanHarness.Sources(dir);
        List<RunDestinationItem> destinations = RunPlanHarness.Destinations(dir);
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(h.SourceDir, s.SourceRoot));
        Assert.All(sources, s => Assert.Equal(OperationKind.Processed, s.Kind));
        // An ordinal is a position in sources.ndjsonl, so every destination resolves to the source whose
        // content lands there.
        Assert.Equal([0, 1], destinations.Select(d => d.SourceOrdinal).Order());
        foreach (RunDestinationItem destination in destinations)
        {
            string sourceName = Path.GetFileName(sources[destination.SourceOrdinal].Path);
            Assert.EndsWith(sourceName, destination.Path, StringComparison.Ordinal);
        }
        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(sources.Count, header.SourceItemCount);
        Assert.Equal(destinations.Count, header.DestinationItemCount);
    }

    [Fact]
    public async Task An_ALREADY_SYNCHRONIZED_profile_still_records_every_source_it_looked_at()
    {
        // The reported symptom, at its source: a Mirror profile whose files are all already at the target
        // has NO copy items, so a source list read from copies.ndjsonl is empty and the user cannot tell
        // "everything is up to date" from "the scan found nothing".
        using RunPlanHarness h = new("snap-source-synced");
        h.WriteSource("same.txt", "identical");
        h.WriteTarget("same.txt", "identical");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        Assert.Empty(RunPlanHarness.Copies(dir));
        RunSourceItem source = Assert.Single(RunPlanHarness.Sources(dir));
        Assert.EndsWith("same.txt", source.Path, StringComparison.Ordinal);
        Assert.Equal(OperationKind.SkippedUnchanged, source.Kind);
        // No disposition on a file that will not be processed, or it would count toward the disposal total
        // for work that never happens.
        Assert.Null(source.Disposition);
    }

    [Fact]
    public async Task A_FILTERED_source_is_recorded_with_the_rule_that_excluded_it()
    {
        using RunPlanHarness h = new("snap-source-filtered");
        h.WriteSource("keep.txt", "keep");
        h.WriteSource("drop.tmp", "drop");

        (string dir, _, _) = await h.PlanAsync(
            h.AdditiveProfile() with { Filters = new FilterSet { ExcludeGlob = ["*.tmp"] } });

        Assert.Equal(2, RunPlanHarness.Sources(dir).Count);
        RunSourceItem dropped = Assert.Single(
            RunPlanHarness.Sources(dir), s => s.Path.EndsWith("drop.tmp", StringComparison.Ordinal));
        Assert.Equal(OperationKind.SkippedByFilter, dropped.Kind);
        Assert.NotNull(dropped.Detail);
    }

    [Fact]
    public async Task A_source_items_disposition_is_what_makes_a_trashed_original_legible()
    {
        using RunPlanHarness h = new("snap-source-disposition");
        h.WriteSource("moved.txt", "content");
        Profile profile = h.AdditiveProfile();

        (string dir, _, _) = await h.PlanAsync(profile with
        {
            Policies = profile.Policies with { OnSuccess = OnSuccessAction.MoveToTrash },
        });

        Assert.Equal(OnSuccessAction.MoveToTrash, Assert.Single(RunPlanHarness.Sources(dir)).Disposition);
    }

    [Fact]
    public async Task A_destination_that_already_exists_records_the_file_it_acts_on()
    {
        // Size and mtime of the EXISTING file, which is what makes an overwrite legible as destructive.
        using RunPlanHarness h = new("snap-dest-subject");
        h.WriteSource("clash.txt", "new content");
        h.WriteTarget("clash.txt", "old");

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(scanDestination: true));

        RunDestinationItem item = Assert.Single(RunPlanHarness.Destinations(dir));
        Assert.NotNull(item.SubjectPath);
        Assert.EndsWith("clash.txt", item.SubjectPath, StringComparison.Ordinal);
        Assert.Equal(3, item.SubjectSizeBytes);
        Assert.NotNull(item.SubjectLastWriteUtc);
    }

    [Fact]
    public async Task A_destination_that_does_not_exist_yet_records_NO_subject()
    {
        using RunPlanHarness h = new("snap-dest-no-subject");
        h.WriteSource("fresh.txt", "fresh");

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(scanDestination: true));

        RunDestinationItem item = Assert.Single(RunPlanHarness.Destinations(dir));
        Assert.Equal(OperationKind.New, item.Kind);
        Assert.Null(item.SubjectPath);
        Assert.Null(item.SubjectSizeBytes);
    }

    [Fact]
    public async Task An_ORPHAN_is_recorded_as_a_deletion_and_NOT_also_as_a_destination()
    {
        // Counted twice, a client reading both files would report one orphan as two — and the deletion
        // pass's whole reason for a separate small file is that it never has to read the other halves.
        using RunPlanHarness h = new("snap-dest-no-orphans");
        h.WriteSource("keep.txt", "keep");
        h.WriteTarget("orphan.txt", "orphaned");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        Assert.Equal("orphan.txt", Path.GetFileName(Assert.Single(RunPlanHarness.Deletes(dir)).Path));
        List<RunDestinationItem> destinations = RunPlanHarness.Destinations(dir);
        Assert.DoesNotContain(destinations, d => d.Kind == OperationKind.Deleted);
        Assert.DoesNotContain(destinations, d => d.Path.EndsWith("orphan.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unchanged_sources_destination_is_still_attributed_to_it()
    {
        // An unchanged file writes no copy item but still projects a destination — that is what keeps its
        // target off the orphan list. Because the ordinal indexes the SOURCE list rather than the copy
        // list, that destination is still attributed to the file it belongs to instead of floating free.
        using RunPlanHarness h = new("snap-dest-unchanged");
        h.WriteSource("same.txt", "identical");
        h.WriteTarget("same.txt", "identical");

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(scanDestination: true));

        Assert.Empty(RunPlanHarness.Copies(dir));
        Assert.Single(RunPlanHarness.Sources(dir));
        Assert.Equal(0, Assert.Single(RunPlanHarness.Destinations(dir)).SourceOrdinal);
    }

    // ---- delete items (Mirror orphans) -----------------------------------------------------------

    [Fact]
    public async Task A_target_file_no_source_writes_to_becomes_a_delete_item()
    {
        using RunPlanHarness h = new("snap-orphan");
        h.WriteSource("kept.txt", "kept");
        string orphan = h.WriteTarget("orphan.txt", "orphaned");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        RunDeleteItem only = Assert.Single(RunPlanHarness.Deletes(dir));
        Assert.Equal(orphan, only.Path);
        Assert.Equal(h.TargetDir, only.TargetRoot);
        Assert.Equal(8, only.SizeBytes);
        // The timestamp is not decoration: the deletion pass re-stats under the lock and refuses any
        // path whose last-write no longer matches what the user approved.
        Assert.Equal(
            File.GetLastWriteTimeUtc(orphan),
            only.LastWriteUtc.UtcDateTime,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_target_file_a_source_DOES_write_to_is_never_a_delete_item()
    {
        using RunPlanHarness h = new("snap-survivor");
        h.WriteSource("shared.txt", "new content");
        h.WriteTarget("shared.txt", "old content");
        h.WriteTarget("orphan.txt", "orphaned");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        RunDeleteItem only = Assert.Single(RunPlanHarness.Deletes(dir));
        Assert.EndsWith("orphan.txt", only.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_AdditiveArchive_plan_has_NO_delete_items_even_when_it_sweeps()
    {
        using RunPlanHarness h = new("snap-additive");
        h.WriteSource("a.txt", "a");
        h.WriteTarget("orphan.txt", "orphaned");

        // ScanDestination on: the sweep runs and classifies the pre-existing file, but as Untouched.
        // Additive archive never removes anything at a target — that is the whole distinction.
        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(scanDestination: true));

        Assert.Empty(RunPlanHarness.Deletes(dir));
        Assert.Equal(0, RunPlanHarness.Header(dir).DeleteItemCount);
    }

    [Fact]
    public async Task A_destination_copy_of_a_filter_EXCLUDED_source_file_IS_a_delete_item()
    {
        using RunPlanHarness h = new("snap-excluded-orphan");
        h.WriteSource("drop.tmp", "drop");
        string previouslyCopied = h.WriteTarget("drop.tmp", "drop");

        (string dir, _, _) = await h.PlanAsync(
            h.MirrorProfile(filters: new FilterSet { ExcludeGlob = ["*.tmp"] }));

        // DOCUMENTED, USER-VISIBLE CONSEQUENCE: an excluded source file contributes no destination
        // operation, so it contributes no survivor, so its previously-copied destination reads as an
        // orphan and a Mirror run removes it. Tightening a filter on a Mirror profile therefore
        // deletes files it copied before. This matches the dry-run preview exactly, which is why it
        // stands — but it is why the run confirmation has to say so in words.
        RunDeleteItem only = Assert.Single(RunPlanHarness.Deletes(dir));
        Assert.Equal(previouslyCopied, only.Path);
    }

    [Fact]
    public async Task A_deep_orphan_is_a_delete_item_even_below_the_sources_MaxDepth()
    {
        using RunPlanHarness h = new("snap-deep-orphan");
        h.WriteSource("top.txt", "top");
        string deep = h.WriteTarget(Path.Combine("a", "b", "c", "deep.txt"), "deep");

        // The sweep deliberately does not apply MaxDepth: a true mirror removes deep orphans
        // regardless of how shallowly the source is scanned.
        (string dir, _, _) = await h.PlanAsync(
            h.MirrorProfile(filters: new FilterSet { MaxDepth = 1 }));

        Assert.Contains(RunPlanHarness.Deletes(dir), d => d.Path == deep);
    }

    // ---- truncation: the safety flag -------------------------------------------------------------

    // A plan is truncated by ONE thing now: a destination tree the sweep could not finish walking. The
    // file-count bound that used to produce the flag is gone — a plan is the work list a run executes,
    // so a bound that shortened it made a wrong job rather than a small one. These tests reach the flag
    // the only way that is left, by walking a tree deeper than the scan's depth ceiling.

    [Fact]
    public async Task A_plan_whose_sweep_could_not_finish_walking_the_target_is_marked_truncated()
    {
        using RunPlanHarness h = new("snap-truncated", maxScanDepth: 8);
        for (int i = 0; i < 12; i++)
            h.WriteSource($"f{i}.txt", new string('x', i + 1));
        h.WriteTarget("orphan.txt", "orphaned");
        // One level past the ceiling, so the sweep prunes and reports rather than descending.
        h.WriteDeepTarget(depth: 9);

        (string dir, PlanState state, _) = await h.PlanAsync(h.MirrorProfile());

        Assert.True(state.Truncated);
        Assert.NotNull(state.SweepFaultDetail);
        Assert.True(RunPlanHarness.Header(dir).Truncated);
        // NOTE the snapshot still NAMES deletions — the fault is only known once the walk is over, so
        // the entries it did find are already written. The safety property lives one stage later:
        // MirrorDeletionPass refuses any pass whose plan is truncated, whatever the delete list says.
        // See the deletion-gate tests, which drive that refusal directly.
        Assert.Contains(RunPlanHarness.Deletes(dir), d => d.Path.EndsWith("orphan.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_truncated_plan_carries_no_space_projection()
    {
        using RunPlanHarness h = new("snap-trunc-space", maxScanDepth: 8);
        for (int i = 0; i < 12; i++)
            h.WriteSource($"f{i}.txt", "x");
        h.WriteDeepTarget(depth: 9);

        (string dir, PlanState state, _) = await h.PlanAsync(h.MirrorProfile());

        Assert.True(state.Truncated);
        // Totals over a partial graph would be unsound, and "will it fit" must never be answered
        // optimistically.
        Assert.Null(RunPlanHarness.Header(dir).Space);
    }

    [Fact]
    public async Task An_untruncated_plan_carries_a_space_projection()
    {
        using RunPlanHarness h = new("snap-space");
        h.WriteSource("a.txt", "aaaa");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        SpaceProjection? space = RunPlanHarness.Header(dir).Space;
        Assert.NotNull(space);
    }

    // ---- the embedded profile --------------------------------------------------------------------

    [Fact]
    public async Task The_snapshot_embeds_the_profile_so_a_later_edit_cannot_change_what_runs()
    {
        using RunPlanHarness h = new("snap-profile");
        h.WriteSource("a.txt", "a");
        Profile planned = h.MirrorProfile(timing: MirrorDeletion.Proactive);

        (string dir, _, _) = await h.PlanAsync(planned);

        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(planned.Id, header.Profile.Id);
        Assert.Equal(SyncMode.Mirror, header.Profile.SyncMode);
        // Round-tripped through the snapshot's own serializer, so the policies the run executes under
        // are the ones captured at plan time — not whatever the catalog holds by the time it runs.
        Assert.Equal(MirrorDeletion.Proactive, header.Profile.Policies.MirrorDeletion);
        Assert.Equal(planned.Policies.VerificationMethod, header.Profile.Policies.VerificationMethod);
        Assert.Equal(planned.Sources[0].Path, header.Profile.Sources[0].Path);
        Assert.Equal(planned.Targets[0].Path, header.Profile.Targets[0].Path);
    }

    [Fact]
    public async Task A_scoped_plan_records_its_scope()
    {
        using RunPlanHarness h = new("snap-scope");
        h.WriteSource(Path.Combine("sub", "a.txt"), "a");
        h.WriteSource("top.txt", "t");
        string scope = Path.Combine(h.SourceDir, "sub");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), scopePath: scope);

        // Recorded because a narrowed run's work list covers only part of the source set, which is
        // exactly why Mirror deletion must refuse to act on one.
        Assert.Equal(scope, RunPlanHarness.Header(dir).ScopePath);
        Assert.Single(RunPlanHarness.Copies(dir));
    }

    // ---- durability of the format ----------------------------------------------------------------

    [Fact]
    public async Task A_corrupted_item_line_is_skipped_rather_than_read_as_a_deletion()
    {
        using RunPlanHarness h = new("snap-corrupt");
        h.WriteSource("kept.txt", "kept");
        h.WriteTarget("orphan1.txt", "one");
        h.WriteTarget("orphan2.txt", "two");
        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());
        string deletes = Path.Combine(dir, "deletes.ndjsonl");
        string[] lines = File.ReadAllLines(deletes);
        Assert.Equal(2, lines.Length);

        // Flip a byte inside the framed payload so the CRC no longer matches.
        lines[0] = lines[0][..^3] + "XY\"";
        File.WriteAllLines(deletes, lines);

        // The checksum is what stops a damaged line from becoming a deletion of the wrong path. A
        // dropped deletion merely leaves an orphan behind; a mis-read one destroys a live file.
        Assert.Single(RunPlanHarness.Deletes(dir));
    }

    [Fact]
    public async Task A_snapshot_with_no_orphans_still_reads_back_cleanly()
    {
        using RunPlanHarness h = new("snap-empty-deletes");
        h.WriteSource("a.txt", "a");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile());

        Assert.Empty(RunPlanHarness.Deletes(dir));
        Assert.Single(RunPlanHarness.Copies(dir));
    }

    // ---- blast-radius counts ---------------------------------------------------------------------
    // Recorded by the writer during the walk that produces the item files, so a client can show the three
    // figures that decide whether a plan is safe to approve WITHOUT replaying the plan. The Preview tab
    // folds the same three from the streamed rows; these tests exist because two surfaces showing one
    // plan's blast radius from different sources must not disagree about it.

    /// <summary>An existing destination file the plan will overwrite. Counted once, and — the point of the
    /// test — recorded on the header, where a summary can read it in O(1).</summary>
    [Fact]
    public async Task The_header_counts_the_destinations_this_plan_will_overwrite()
    {
        using RunPlanHarness h = new("snap-overwrite");
        h.WriteSource("a.txt", "new content");
        h.WriteTarget("a.txt", "old");                 // different content, so it is a real overwrite
        h.WriteSource("b.txt", "no clash of its own");

        // The harness's default conflict policy is Skip, which is its own kind — ask for Overwrite, which
        // is what this case is about.
        Profile profile = h.AdditiveProfile(scanDestination: true);
        (string dir, _, _) = await h.PlanAsync(
            profile with
            {
                Policies = profile.Policies with { ConflictResolution = ConflictResolution.Overwrite },
            });

        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(1, header.OverwriteCount);
        Assert.Equal(0, header.RenameCount);
        // The Preview tab derives its chip from the destination operations; the writer's count must equal
        // what those rows say, or the two surfaces disagree.
        Assert.Equal(
            RunPlanHarness.Destinations(dir).Count(d => d.Kind == OperationKind.Overwrite),
            header.OverwriteCount);
    }

    /// <summary>A copy written under a suffixed name because something was already at its destination.</summary>
    [Fact]
    public async Task The_header_counts_the_copies_this_plan_will_rename()
    {
        using RunPlanHarness h = new("snap-rename");
        h.WriteSource("a.txt", "new content");
        h.WriteTarget("a.txt", "old");

        Profile profile = h.AdditiveProfile(scanDestination: true);
        (string dir, _, _) = await h.PlanAsync(
            profile with
            {
                Policies = profile.Policies with { ConflictResolution = ConflictResolution.RenameSuffix },
            });

        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(1, header.RenameCount);
        Assert.Equal(0, header.OverwriteCount);
        Assert.Equal(
            RunPlanHarness.Destinations(dir).Count(d => d.Kind == OperationKind.Rename),
            header.RenameCount);
    }

    /// <summary>Sources the run will move or delete once copied — the count that says the run costs the
    /// user something they still had.</summary>
    [Fact]
    public async Task The_header_counts_the_sources_this_plan_will_dispose_of()
    {
        using RunPlanHarness h = new("snap-disposal");
        h.WriteSource("a.txt", "a");
        h.WriteSource("b.txt", "b");

        (string dir, _, _) = await h.PlanAsync(
            h.AdditiveProfile() with
            {
                Policies = TestProfiles.DefaultPolicies() with { OnSuccess = OnSuccessAction.MoveToTrash },
            });

        Assert.Equal(2, RunPlanHarness.Header(dir).DisposalCount);
    }

    /// <summary>KeepSource is the only NON-destructive disposition, and it must not be counted — a profile
    /// that leaves its sources alone showing "2 source disposals" would be a false alarm on the one figure
    /// the reader is most entitled to trust.</summary>
    [Fact]
    public async Task Keeping_the_sources_counts_no_disposals()
    {
        using RunPlanHarness h = new("snap-keep");
        h.WriteSource("a.txt", "a");
        h.WriteSource("b.txt", "b");

        (string dir, _, _) = await h.PlanAsync(
            h.AdditiveProfile() with
            {
                Policies = TestProfiles.DefaultPolicies() with { OnSuccess = OnSuccessAction.KeepSource },
            });

        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(0, header.DisposalCount);
        Assert.Equal(2, header.SourceItemCount);   // both files were still scanned
    }

    /// <summary>A file the filters excluded produces no work, so it must not be counted as a disposal
    /// either — its disposition is null precisely so it stays out of this figure.</summary>
    [Fact]
    public async Task A_filtered_source_counts_no_disposal()
    {
        using RunPlanHarness h = new("snap-filtered-disposal");
        h.WriteSource("keep.txt", "a");
        h.WriteSource("skip.tmp", "b");

        (string dir, _, _) = await h.PlanAsync(
            h.AdditiveProfile() with
            {
                Filters = new FilterSet { ExcludeGlob = ["*.tmp"] },
                Policies = TestProfiles.DefaultPolicies() with { OnSuccess = OnSuccessAction.PermanentDelete },
            });

        RunSnapshotHeader header = RunPlanHarness.Header(dir);
        Assert.Equal(2, header.SourceItemCount);   // both were looked at
        Assert.Equal(1, header.CopyItemCount);     // only one produces work
        Assert.Equal(1, header.DisposalCount);     // and only that one is destroyed
    }

    [Fact]
    public void Reading_a_snapshot_that_does_not_exist_is_a_typed_failure_not_a_throw()
    {
        Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(
            Path.Combine(Path.GetTempPath(), "fm-snap-missing-" + Guid.NewGuid().ToString("N")));

        Assert.True(read.TryGetError(out string? error));
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
    }
}
