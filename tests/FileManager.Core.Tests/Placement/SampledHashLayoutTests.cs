using FileManager.Core.Placement;

namespace FileManager.Core.Tests.Placement;

/// <summary>The window geometry the sampled digest depends on. These are the properties that make a
/// sampled comparison sound: a bounded read, no overlapping windows, an exact tail (so truncation is
/// always visible), and honest byte accounting for the run counters.</summary>
public sealed class SampledHashLayoutTests
{
    private static readonly SampledHashLayout Layout = SampledHashLayout.Default;

    [Fact]
    public void The_default_layout_reads_eight_mebibytes()
    {
        Assert.Equal(1024 * 1024, Layout.WindowSizeBytes);
        Assert.Equal(6, Layout.InteriorWindowCount);
        Assert.Equal(8L * 1024 * 1024, Layout.BudgetBytes);
    }

    [Theory]
    [InlineData(64L * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024 * 1024)]
    [InlineData(long.MaxValue / 2)]
    public void Bytes_read_is_the_budget_however_large_the_file(long length)
    {
        // The whole point: read cost is flat in file size.
        Assert.Equal(Layout.BudgetBytes, Layout.BytesRead(length));
    }

    [Fact]
    public void Bytes_read_never_exceeds_a_small_file()
    {
        Assert.Equal(1024, Layout.BytesRead(1024));
        Assert.Equal(0, Layout.BytesRead(0));
        Assert.Equal(0, Layout.BytesRead(-1));   // defensive: a bogus length must not report negative I/O
    }

    [Fact]
    public void A_file_at_or_below_the_budget_is_covered_whole_and_has_no_windows()
    {
        Assert.True(Layout.CoversWholeFile(Layout.BudgetBytes));
        Assert.Empty(Layout.WindowOffsets(Layout.BudgetBytes));
        Assert.False(Layout.CoversWholeFile(Layout.BudgetBytes + 1));
    }

    [Theory]
    [InlineData(8L * 1024 * 1024 + 1)]            // the very first sampled size — the tightest packing
    [InlineData(64L * 1024 * 1024)]
    [InlineData(700L * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024 * 1024)]
    [InlineData(120L * 1024 * 1024 * 1024)]
    public void Windows_are_in_order_inside_the_file_and_never_overlap(long length)
    {
        long[] offsets = Layout.WindowOffsets(length);
        long w = Layout.WindowSizeBytes;

        Assert.Equal(Layout.InteriorWindowCount + 2, offsets.Length);
        Assert.Equal(0, offsets[0]);
        Assert.Equal(length - w, offsets[^1]);              // exact tail — catches truncation

        for (int i = 1; i < offsets.Length; i++)
        {
            Assert.True(offsets[i] >= offsets[i - 1] + w,
                $"window {i} at {offsets[i]} overlaps window {i - 1} at {offsets[i - 1]} (W={w}, L={length})");
        }
        Assert.True(offsets[^1] + w <= length, "the tail window must not read past EOF");
    }

    [Fact]
    public void Interior_windows_are_aligned_to_the_window_size()
    {
        // Aligned reads are friendlier to the I/O stack. The head is 0 (aligned by definition) and the
        // tail is deliberately exact rather than aligned, so only the interior is checked.
        long[] offsets = Layout.WindowOffsets(9L * 1024 * 1024 * 1024 + 12345);
        for (int i = 1; i <= Layout.InteriorWindowCount; i++)
            Assert.Equal(0, offsets[i] % Layout.WindowSizeBytes);
    }

    [Fact]
    public void Windows_spread_across_the_file_rather_than_clustering()
    {
        // Guards against a regression that collapses the interior offsets toward one end, which would
        // quietly make the digest far weaker without failing anything else.
        const long length = 8L * 1024 * 1024 * 1024;
        long[] offsets = Layout.WindowOffsets(length);

        Assert.True(offsets[1] < length / 4, "the first interior window should be in the first quarter");
        Assert.True(offsets[Layout.InteriorWindowCount] > length / 2,
            "the last interior window should be past the midpoint");
    }
}
