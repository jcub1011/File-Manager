using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>The job queue's summary pane: projecting one run's frozen plan into what the pane shows.
///
/// <para>The load states are the interesting half. "No plan yet" is the NORMAL answer for a run being
/// watched mid-scan — the snapshot header a summary is read from is written when planning finishes — so it
/// must be held apart from a genuine failure, or the pane shows an error for something that is simply not
/// ready.</para></summary>
public sealed class RunSummaryPaneViewModelTests
{
    private static RunDetailDto Detail(
        Profile? profile = null, string? scope = null, bool truncated = false,
        int copies = 4, long copyBytes = 2048, int deletes = 0, long deleteBytes = 0,
        int sources = 9, int overwrites = 0, int renames = 0, int disposals = 0,
        SpaceProjection? space = null) =>
        new(Guid.NewGuid(), profile ?? ProfileFactory.Sample(), scope, DateTimeOffset.UnixEpoch,
            copies, copyBytes, deletes, deleteBytes, sources, DestinationItemCount: 4,
            overwrites, renames, disposals, truncated, SweepFaultDetail: null, space);

    // ── Load states ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_pane_shows_neither_a_plan_nor_an_error()
    {
        RunSummaryPaneViewModel pane = new();

        Assert.False(pane.HasPlan);
        Assert.False(pane.HasNoPlanYet);
        Assert.False(pane.IsLoading);
        Assert.Null(pane.ErrorText);
    }

    /// <summary>RUN_PLAN_UNAVAILABLE is not a failure. It is what a run still planning answers, and the pane
    /// has to say "still working out what this will do" rather than show a red bar.</summary>
    [Fact]
    public void RUN_PLAN_UNAVAILABLE_is_reported_as_no_plan_yet_not_as_an_error()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Fail(new IpcError("RUN_PLAN_UNAVAILABLE", "the run has no plan snapshot yet"));

        Assert.True(pane.HasNoPlanYet);
        Assert.Null(pane.ErrorText);
        Assert.Null(pane.Plan);
        Assert.False(pane.IsLoading);
    }

    /// <summary>RUN_NOT_FOUND is swallowed everywhere else in the queue — there it means a click raced a run
    /// that had already gone, and the row is about to disappear anyway. Here the row is still selected and in
    /// front of the user, so an empty pane with no explanation would just look broken.</summary>
    [Fact]
    public void RUN_NOT_FOUND_is_explained_rather_than_swallowed()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Fail(new IpcError("RUN_NOT_FOUND", "no run with id"));

        Assert.False(pane.HasNoPlanYet);
        Assert.Equal("That run is no longer in the queue.", pane.ErrorText);
    }

    [Fact]
    public void Any_other_failure_carries_the_services_own_message()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Fail(new IpcError("SERVICE_UNAVAILABLE", "the service is not running"));

        Assert.Contains("the service is not running", pane.ErrorText);
    }

    /// <summary>Every transition must leave exactly one state true. A pane that kept the previous run's plan
    /// while loading the next would show one run's counts under another run's name.</summary>
    [Fact]
    public void Each_transition_clears_the_state_before_it()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail());
        Assert.True(pane.HasPlan);

        pane.BeginLoading();
        Assert.False(pane.HasPlan);
        Assert.True(pane.IsLoading);

        pane.Fail(new IpcError("RUN_PLAN_UNAVAILABLE", "x"));
        Assert.False(pane.IsLoading);
        Assert.True(pane.HasNoPlanYet);

        pane.Show(Detail());
        Assert.True(pane.HasPlan);
        Assert.False(pane.HasNoPlanYet);

        pane.Clear();
        Assert.False(pane.HasPlan);
        Assert.False(pane.HasNoPlanYet);
        Assert.Null(pane.ErrorText);
    }

    // ── The projection ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plan_lists_the_profiles_own_sources_and_targets()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail());

        Assert.Equal([@"C:\ui-test\src"], pane.Plan!.Sources.Select(r => r.Path));
        Assert.Equal([@"C:\ui-test\dst"], pane.Plan.Targets.Select(r => r.Path));
    }

    /// <summary>A per-source filter override is the one thing about a source that changes which files this
    /// run touched, so it is noted beside the path.</summary>
    [Fact]
    public void A_source_with_its_own_filters_is_noted()
    {
        Profile profile = ProfileFactory.Sample();
        profile = profile with
        {
            Sources =
            [
                profile.Sources[0],
                new SourceConfig { Path = @"C:\other", Filters = new FilterSet { ExcludeGlob = ["*.bak"] } },
            ],
        };
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(profile));

        Assert.False(pane.Plan!.Sources[0].HasNote);
        Assert.True(pane.Plan.Sources[1].HasNote);
        Assert.Equal("has its own filters", pane.Plan.Sources[1].Note);
    }

    /// <summary>The first chip is FILES SCANNED, not the copy count — that distinction is what lets an
    /// already-synchronized profile read as "9 scanned, 0 to copy" (up to date) rather than as a plan that
    /// found nothing.</summary>
    [Fact]
    public void The_first_chip_counts_every_file_the_plan_looked_at()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(sources: 1204, copies: 0, copyBytes: 0));

        Assert.Equal("1,204", pane.Plan!.Chips[0].Text);
        Assert.Equal("IconDocument", pane.Plan.Chips[0].IconKey);
        Assert.Equal("0 (0 B)", pane.Plan.Chips[1].Text);
    }

    /// <summary>A zero count contributes no chip at all, rather than a chip reading zero — the Preview tab's
    /// strip hides its zeros for the same reason, and six chips of which four say nothing is noise.</summary>
    [Fact]
    public void A_plan_with_no_blast_radius_shows_only_the_two_counts_and_an_all_clear()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail());

        Assert.Equal(3, pane.Plan!.Chips.Count);
        Assert.Equal("IconCheckmark", pane.Plan.Chips[2].IconKey);
        Assert.Equal("Brush.Success", pane.Plan.Chips[2].ColorKey);
    }

    /// <summary>...and where there IS a blast radius, the all-clear must NOT appear beside it. A checkmark
    /// next to "3 will be overwritten" is the worst possible reading.</summary>
    [Fact]
    public void A_destructive_plan_shows_its_counts_and_no_all_clear()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(overwrites: 3, renames: 1, disposals: 12, deletes: 2, deleteBytes: 512));

        Assert.DoesNotContain(pane.Plan!.Chips, c => c.IconKey == "IconCheckmark");
        Assert.Contains(pane.Plan.Chips, c => c.IconKey == "IconOverwrite" && c.Text == "3");
        Assert.Contains(pane.Plan.Chips, c => c.IconKey == "IconRename" && c.Text == "1");
        Assert.Contains(pane.Plan.Chips, c => c.Text == "12");
        Assert.Contains(pane.Plan.Chips, c => c.Text == "2 (512 B)");
    }

    /// <summary>A truncated plan has no space projection — totals over a partial graph would be unsound — and
    /// the pane must handle the null rather than throw, because that is the state a large or faulted scan
    /// actually lands in.</summary>
    [Fact]
    public void A_truncated_plan_has_no_storage_forecast_and_says_why_it_is_truncated()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(truncated: true, space: null));

        Assert.False(pane.Plan!.HasSpace);
        Assert.Null(pane.Plan.Space);
        Assert.True(pane.Plan.IsTruncated);
        Assert.Contains("nothing will be removed", pane.Plan.TruncationText);
    }

    [Fact]
    public void A_space_projection_is_handed_to_the_shared_storage_view_model()
    {
        SpaceProjection projection = new()
        {
            SafetyMarginBytes = 1024,
            TotalBytesWritten = 5000,
            TotalNetChangeBytes = 400,
            Volumes =
            [
                new VolumeSpaceEstimate
                {
                    VolumeRoot = "D:", CapacityKnown = true, TotalCapacityBytes = 100_000,
                    UsedNowBytes = 40_000, FreeNowBytes = 60_000, BytesWrittenBytes = 5000,
                    SettledUsedBytes = 40_400, RealisticPeakUsedBytes = 40_600,
                    SafeCeilingUsedBytes = 41_000,
                },
            ],
        };
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(space: projection));

        // The SAME type the Preview tab renders its forecast from, which is what keeps "will it fit"
        // identical wherever it is asked.
        Assert.True(pane.Plan!.HasSpace);
        Assert.Equal("D:", Assert.Single(pane.Plan.Space!.Volumes).VolumeRoot);
    }

    /// <summary>A narrowed run behaves differently in a way no count reveals: its orphan set is unsound over
    /// a partial source tree, so a Mirror run scoped to a subfolder copies but removes nothing — and the
    /// deletion count just reads zero. Hence a line of its own.</summary>
    [Fact]
    public void A_scoped_run_says_it_covers_only_part_of_the_profile()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(scope: @"C:\ui-test\src\sub"));

        Assert.True(pane.Plan!.HasScope);
        Assert.Contains(@"C:\ui-test\src\sub", pane.Plan.ScopeText);
        Assert.Contains("not the whole profile", pane.Plan.ScopeText);
    }

    [Fact]
    public void A_whole_profile_run_says_nothing_about_scope()
    {
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail());

        Assert.False(pane.Plan!.HasScope);
        Assert.Null(pane.Plan.ScopeText);
    }

    // ── Settings ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mirror deletion is offered ONLY under Mirror. On an additive profile the setting means
    /// nothing, and a row reading "After copy" beside a plan that removes nothing implies it removes
    /// something.</summary>
    [Fact]
    public void Mirror_deletion_appears_only_for_a_mirror_profile()
    {
        RunSummaryPaneViewModel additive = new();
        additive.Show(Detail(ProfileFactory.Sample()));
        Assert.DoesNotContain(additive.Plan!.Settings, s => s.Label == "Remove orphans");

        RunSummaryPaneViewModel mirror = new();
        mirror.Show(Detail(ProfileFactory.Sample() with { SyncMode = SyncMode.Mirror }));
        Assert.Contains(mirror.Plan!.Settings, s => s.Label == "Remove orphans");
    }

    /// <summary>The EFFECTIVE value, not the stored flag: Mirror always sweeps whatever ScanDestination
    /// says, so reporting the raw field would tell a Mirror reader "No" about a sweep that certainly
    /// happened.</summary>
    [Fact]
    public void Scan_destinations_reports_what_the_run_actually_did()
    {
        RunSummaryPaneViewModel additive = new();
        additive.Show(Detail(ProfileFactory.Sample() with { ScanDestination = false }));
        Assert.Equal("No", additive.Plan!.Settings.Single(s => s.Label == "Scan destinations").Value);

        RunSummaryPaneViewModel mirror = new();
        mirror.Show(Detail(ProfileFactory.Sample() with
        {
            SyncMode = SyncMode.Mirror,
            ScanDestination = false,   // ignored under Mirror
        }));
        Assert.Contains(
            "always", mirror.Plan!.Settings.Single(s => s.Label == "Scan destinations").Value);
    }

    /// <summary>A destructive disposition earns the danger bar, not a settings row: the originals are the one
    /// thing a run can cost the user something they still had, and a row in a list of eight is skimmable.</summary>
    [Theory]
    [InlineData(OnSuccessAction.KeepSource, false)]
    [InlineData(OnSuccessAction.MoveToArchive, false)]
    [InlineData(OnSuccessAction.MoveToTrash, true)]
    [InlineData(OnSuccessAction.PermanentDelete, true)]
    public void Only_a_destructive_disposition_is_shouted(OnSuccessAction action, bool shouted)
    {
        Profile profile = ProfileFactory.Sample();
        profile = profile with { Policies = profile.Policies with { OnSuccess = action } };
        RunSummaryPaneViewModel pane = new();

        pane.Show(Detail(profile));

        Assert.Equal(shouted, pane.Plan!.ShowDispositionWarning);
        Assert.NotEmpty(pane.Plan.DispositionText);
    }

    // ── Filters ─────────────────────────────────────────────────────────────────────────────────────
    // A plan's file count is only interpretable against what was filtered out of it, so "none" is as
    // load-bearing an answer as a list of globs.

    [Fact]
    public void No_filter_set_reads_as_every_file()
    {
        Assert.Contains("None", RunPlanSummary.DescribeFilters(null));
    }

    /// <summary>A FilterSet can exist with every member unset, which filters nothing. It must read as "none"
    /// and never as "some filters, unspecified".</summary>
    [Fact]
    public void An_empty_filter_set_also_reads_as_every_file()
    {
        Assert.Contains("None", RunPlanSummary.DescribeFilters(new FilterSet()));
    }

    [Fact]
    public void A_filter_set_names_each_rule_it_actually_carries()
    {
        string text = RunPlanSummary.DescribeFilters(new FilterSet
        {
            Include = ["*.wav"],
            ExcludeGlob = ["*.tmp"],
            MinSizeBytes = 1024,
            MaxDepth = 3,
        });

        Assert.Contains("include *.wav", text);
        Assert.Contains("exclude *.tmp", text);
        Assert.Contains("at least", text);
        Assert.Contains("3 level(s) deep", text);
        Assert.DoesNotContain("None", text);
    }

    /// <summary>The three attribute flags are named only when ON: they default to off, and listing
    /// "hidden: no" for every profile would bury the one case that matters.</summary>
    [Fact]
    public void Attribute_flags_are_named_only_when_they_are_on()
    {
        Assert.Contains("None", RunPlanSummary.DescribeFilters(new FilterSet
        {
            Attributes = new AttributeFilterSettings(),
        }));

        string text = RunPlanSummary.DescribeFilters(new FilterSet
        {
            Attributes = new AttributeFilterSettings { IncludeHidden = true, FollowSymlinks = true },
        });
        Assert.Contains("including hidden files", text);
        Assert.Contains("following symlinks", text);
        Assert.DoesNotContain("including system files", text);
    }
}
