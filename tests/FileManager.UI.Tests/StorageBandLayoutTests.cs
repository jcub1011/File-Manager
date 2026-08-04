using System.Linq;
using FileManager.UI.Controls;
using Xunit;

namespace FileManager.UI.Tests;

/// <summary>The storage bar layers its bands largest-first, so correctness is a property of the
/// ordering rather than of any particular case: whatever the figures, the bands must come out sorted,
/// their visible spans must tile the bar end to end without gaps or overlap, and each figure must own
/// the edge it names.</summary>
public sealed class StorageBandLayoutTests
{
    private static double Span(StorageBand[] bands, StorageBandKind kind)
    {
        StorageBand b = bands.Single(x => x.Kind == kind);
        return b.Threshold - b.VisibleFrom;
    }

    private static void AssertTiles(StorageBand[] bands)
    {
        // Sorted largest-first...
        Assert.Equal(
            bands.Select(b => b.Threshold).OrderByDescending(t => t).ToArray(),
            bands.Select(b => b.Threshold).ToArray());

        // ...and each band picks up exactly where the next-smaller one stops, down to zero.
        for (int i = 0; i < bands.Length - 1; i++)
            Assert.Equal(bands[i + 1].Threshold, bands[i].VisibleFrom);
        Assert.Equal(0, bands[^1].VisibleFrom);
    }

    [Fact]
    public void A_growing_run_reads_current_then_at_rest_then_the_two_peaks()
    {
        StorageBand[] bands = StorageBandLayout.Compute(
            usedNow: 500, settled: 900, realisticPeak: 950, safeCeiling: 980);

        AssertTiles(bands);
        Assert.Equal(
            [StorageBandKind.AbsolutePeak, StorageBandKind.RealisticPeak, StorageBandKind.UsedAtRest, StorageBandKind.CurrentUsed],
            bands.Select(b => b.Kind));

        Assert.Equal(500, Span(bands, StorageBandKind.CurrentUsed));      // 0 → 500, untouched baseline
        Assert.Equal(400, Span(bands, StorageBandKind.UsedAtRest));       // 500 → 900, the growth
        Assert.Equal(50, Span(bands, StorageBandKind.RealisticPeak));     // 900 → 950, transient
        Assert.Equal(30, Span(bands, StorageBandKind.AbsolutePeak));      // 950 → 980, worst case
        Assert.DoesNotContain(bands, b => b.Kind == StorageBandKind.Freed);
    }

    [Fact]
    public void A_run_that_frees_space_but_peaks_above_the_starting_mark_hides_the_freed_band_behind_the_peak()
    {
        // The regression this layering exists for: settled (900) is below usedNow (1000), yet the run
        // climbs to 950/980 on the way there because it holds files it has not deleted yet. The freed
        // band must not claim space the run is still occupying — only the sliver above the worst case.
        StorageBand[] bands = StorageBandLayout.Compute(
            usedNow: 1000, settled: 900, realisticPeak: 950, safeCeiling: 980);

        AssertTiles(bands);
        Assert.Equal(
            [StorageBandKind.Freed, StorageBandKind.AbsolutePeak, StorageBandKind.RealisticPeak, StorageBandKind.CurrentUsed],
            bands.Select(b => b.Kind));

        Assert.Equal(900, Span(bands, StorageBandKind.CurrentUsed));      // 0 → 900, what stays put
        Assert.Equal(50, Span(bands, StorageBandKind.RealisticPeak));     // 900 → 950
        Assert.Equal(30, Span(bands, StorageBandKind.AbsolutePeak));      // 950 → 980
        Assert.Equal(20, Span(bands, StorageBandKind.Freed));             // 980 → 1000, genuinely released
    }

    [Fact]
    public void A_run_that_frees_space_and_stays_below_the_starting_mark_shows_the_whole_release()
    {
        StorageBand[] bands = StorageBandLayout.Compute(
            usedNow: 1000, settled: 500, realisticPeak: 600, safeCeiling: 650);

        AssertTiles(bands);
        Assert.Equal(500, Span(bands, StorageBandKind.CurrentUsed));
        Assert.Equal(100, Span(bands, StorageBandKind.RealisticPeak));
        Assert.Equal(50, Span(bands, StorageBandKind.AbsolutePeak));
        Assert.Equal(350, Span(bands, StorageBandKind.Freed));            // 650 → 1000
    }

    [Fact]
    public void Coinciding_figures_collapse_to_zero_width_bands_rather_than_inverted_ones()
    {
        // A run with no transient overhead and no worst-case headroom: three thresholds land together.
        StorageBand[] bands = StorageBandLayout.Compute(
            usedNow: 500, settled: 900, realisticPeak: 900, safeCeiling: 900);

        AssertTiles(bands);
        Assert.All(bands, b => Assert.True(b.Threshold >= b.VisibleFrom));
        Assert.False(bands.Single(b => b.Kind == StorageBandKind.RealisticPeak).IsVisible);
        Assert.False(bands.Single(b => b.Kind == StorageBandKind.AbsolutePeak).IsVisible);
        Assert.True(bands.Single(b => b.Kind == StorageBandKind.UsedAtRest).IsVisible);
    }

    [Fact]
    public void An_unchanged_volume_is_one_band()
    {
        StorageBand[] bands = StorageBandLayout.Compute(
            usedNow: 400, settled: 400, realisticPeak: 400, safeCeiling: 400);

        AssertTiles(bands);
        StorageBand only = Assert.Single(bands, b => b.IsVisible);
        Assert.Equal(StorageBandKind.CurrentUsed, only.Kind);
        Assert.Equal(400, only.Threshold);
    }

    [Theory]
    // Peak figures below the totals they are defined to bound: the layout must still come out ordered.
    [InlineData(500, 900, 100, 50)]
    [InlineData(1000, 200, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(700, 700, 700, 100)]
    public void Inconsistent_inputs_still_produce_an_ordered_tiling(
        double usedNow, double settled, double realisticPeak, double safeCeiling)
    {
        StorageBand[] bands = StorageBandLayout.Compute(usedNow, settled, realisticPeak, safeCeiling);

        AssertTiles(bands);
        Assert.All(bands, b => Assert.True(b.Threshold >= b.VisibleFrom));

        // The bar still reaches the largest figure it was handed — nothing is silently dropped.
        Assert.Equal(new[] { usedNow, settled, realisticPeak, safeCeiling }.Max(), bands[0].Threshold);
    }
}
