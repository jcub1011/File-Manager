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

    [Fact]
    public async Task A_plan_that_hit_its_file_bound_is_marked_truncated_and_names_NO_deletions()
    {
        using RunPlanHarness h = new("snap-truncated");
        for (int i = 0; i < 12; i++)
            h.WriteSource($"f{i}.txt", new string('x', i + 1));
        h.WriteTarget("orphan.txt", "orphaned");

        (string dir, PlanState state, _) = await h.PlanAsync(h.MirrorProfile(), maxFiles: 3);

        Assert.True(state.Truncated);
        Assert.True(RunPlanHarness.Header(dir).Truncated);
        // The survivor set is a prefix, so every "no source writes here" judgement is untrustworthy.
        // The sweep is suppressed entirely rather than allowed to fabricate deletions — this is the
        // single most important safety property of the whole feature.
        Assert.Empty(RunPlanHarness.Deletes(dir));
    }

    [Fact]
    public async Task A_truncated_plan_carries_no_space_projection()
    {
        using RunPlanHarness h = new("snap-trunc-space");
        for (int i = 0; i < 12; i++)
            h.WriteSource($"f{i}.txt", "x");

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), maxFiles: 2);

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

    [Fact]
    public void Reading_a_snapshot_that_does_not_exist_is_a_typed_failure_not_a_throw()
    {
        Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(
            Path.Combine(Path.GetTempPath(), "fm-snap-missing-" + Guid.NewGuid().ToString("N")));

        Assert.True(read.TryGetError(out string? error));
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
    }
}
