using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FileManager.Contracts.DryRun;
using FileManager.UI.Controls;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>The storage forecast panel, lifted out of <c>DryRunView</c> so the Preview tab and the job
/// queue's summary pane render it from one place.
///
/// <para>Hosted directly here, against a <see cref="DryRunSpaceViewModel"/>, because that is the contract
/// both callers rely on: the control's DataContext IS the space model. A regression in that — the
/// re-pointing, or a binding still reaching for a <c>Space.</c> prefix it no longer has — renders an empty
/// card rather than throwing, which is the failure a smoke test has to catch.</para></summary>
[Collection(HeadlessCollection.Name)]
public sealed class StoragePanelViewSmokeTests(HeadlessSessionFixture headless)
{
    private static DryRunSpaceViewModel Space(bool warn = false, bool folders = true) =>
        new(new SpaceProjection
        {
            SafetyMarginBytes = 1024,
            TotalBytesWritten = 5000,
            TotalNetChangeBytes = 400,
            Volumes =
            [
                new VolumeSpaceEstimate
                {
                    VolumeRoot = "D:", CapacityKnown = true, TotalCapacityBytes = 100_000,
                    UsedNowBytes = 40_000, FreeNowBytes = 60_000, ClusterBytes = 4096,
                    BytesWrittenBytes = 5000, NetChangeBytes = 400, SettledUsedBytes = 40_400,
                    DeferredReclaimBytes = 200, MirrorDeferredReclaimBytes = 100,
                    // Over capacity − margin when asked to warn, which is what opens the panel and adds
                    // the badge.
                    RealisticPeakUsedBytes = warn ? 99_500 : 40_600,
                    SafeCeilingUsedBytes = warn ? 99_900 : 41_000,
                    // SHORT roots, deliberately. A long path makes the drill-down's content wide all by
                    // itself, which hides the very thing the width test is about — an auto-sized content
                    // presenter only looks wrong when its content is narrow.
                    Folders = folders
                        ?
                        [
                            new FolderSpaceBreakdown
                            {
                                Root = @"D:\a", BytesWrittenBytes = 4000, NetChangeBytes = 300, FileCount = 1,
                            },
                            new FolderSpaceBreakdown
                            {
                                Root = @"D:\b", BytesWrittenBytes = 1000, NetChangeBytes = 100, FileCount = 2,
                            },
                        ]
                        : [],
                },
                // Capacity unknown: the bar hides and a sentence replaces it.
                new VolumeSpaceEstimate { VolumeRoot = "Z:", CapacityKnown = false, BytesWrittenBytes = 10 },
            ],
        });

    [Fact]
    public async Task The_panel_lays_out_its_volumes_and_bars()
    {
        await headless.Session.DispatchAsync(() =>
        {
            (Window window, StoragePanelView panel) = Host(Space());
            Open(window, panel, 520);

            // One bar per volume whose capacity is known; the other volume gets a sentence instead.
            Assert.Single(panel.GetVisualDescendants().OfType<StorageBar>(), b => b.IsVisible);
            Assert.Equal(2, panel.GetVisualDescendants().OfType<StorageBar>().Count());
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>Collapsed until asked, which is what keeps the forecast from eating the pane. Its content is
    /// therefore NOT realized until then — the reason every test here opens it first.</summary>
    [Fact]
    public async Task The_panel_starts_collapsed_when_nothing_is_at_risk()
    {
        await headless.Session.DispatchAsync(() =>
        {
            (Window window, StoragePanelView panel) = Host(Space());

            Assert.False(panel.GetVisualDescendants().OfType<Expander>().First().IsExpanded);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_volume_at_capacity_risk_opens_the_panel_and_badges_it()
    {
        await headless.Session.DispatchAsync(() =>
        {
            DryRunSpaceViewModel space = Space(warn: true);
            Assert.True(space.HasWarning);
            (Window window, StoragePanelView panel) = Host(space);

            // The auto-open is a OneWay bind on HasWarning, so it must actually be expanded — a forecast
            // that says a drive may not fit is the one case the user must not have to click to see.
            Expander card = panel.GetVisualDescendants().OfType<Expander>().First();
            Assert.True(card.IsExpanded);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>The "By folder" drill-down must occupy the panel's full width.
    ///
    /// <para>Fluent's Expander leaves its content presenter aligned to its content, so the drill-down opened
    /// as a narrow column against the left edge — while the four-column grid inside it had a star column with
    /// nowhere to go, so paths trimmed to nothing and the three figures bunched up with the width of the
    /// panel sitting unused to the right. Measured without the fix: <b>289px of a 700px panel</b>.</para>
    /// <para>Asserted on the arranged rectangle because that is the only place the difference shows —
    /// nothing throws either way.</para></summary>
    [Fact]
    public async Task The_by_folder_drill_down_fills_the_panels_width()
    {
        await headless.Session.DispatchAsync(() =>
        {
            (Window window, StoragePanelView panel) = Host(Space(), width: 700);
            Open(window, panel, 700);

            // The volume card's own expander — the outer one is the Storage card itself. Gated on
            // IsVisible because BOTH volumes realize one and the folderless volume's stays hidden.
            Expander byFolder = panel.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.Header as string == "By folder" && e.IsVisible);
            byFolder.IsExpanded = true;
            Relayout(window, 700);

            ItemsControl folders = byFolder.GetVisualDescendants().OfType<ItemsControl>().First();
            Assert.True(folders.Bounds.Width > 0, "the drill-down was not laid out at all");
            // Within the chrome the expander and the cards around it legitimately consume. The bug this
            // pins made it a fraction of the width, not a few pixels short of it.
            Assert.True(folders.Bounds.Width > panel.Bounds.Width - 80,
                $"the drill-down is {folders.Bounds.Width}px inside a {panel.Bounds.Width}px panel");
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>A volume with no per-folder breakdown hides the drill-down rather than opening onto
    /// nothing.</summary>
    [Fact]
    public async Task A_volume_with_no_folder_breakdown_hides_the_drill_down()
    {
        await headless.Session.DispatchAsync(() =>
        {
            (Window window, StoragePanelView panel) = Host(Space(folders: false));
            Open(window, panel, 520);

            Assert.DoesNotContain(
                panel.GetVisualDescendants().OfType<Expander>(),
                e => e.Header as string == "By folder" && e.IsVisible);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static (Window Window, StoragePanelView Panel) Host(
        DryRunSpaceViewModel space, double width = 520)
    {
        StoragePanelView panel = new() { DataContext = space };
        Window window = new() { Content = panel, Width = width, Height = 480 };
        window.Show();
        Relayout(window, width);
        return (window, panel);
    }

    /// <summary>Opens the Storage card. Its content is inside an Expander, so nothing below the header
    /// exists in the visual tree until it is expanded — which is the whole point of the collapsed default,
    /// and means every assertion about a bar or a drill-down has to come through here.</summary>
    private static void Open(Window window, StoragePanelView panel, double width)
    {
        panel.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
        Relayout(window, width);
    }

    private static void Relayout(Window window, double width)
    {
        window.Measure(new Size(width, 480));
        window.Arrange(new Rect(0, 0, width, 480));
        window.UpdateLayout();
    }
}
