using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real <see cref="JobQueueWindow"/> against a queue holding every row shape and forces
/// a layout pass, so runtime-only XAML failures — a bad <c>x:DataType</c>, an IconConverters typo, a
/// missing icon geometry — fail here rather than in the app.
///
/// <para><b>What earns this its keep now that the window is two panes.</b> It used to be the row buttons'
/// <c>$parent[ItemsControl]</c> command bindings, which nothing but a real layout pass could prove
/// resolved; those buttons moved into the summary pane's footer and bind directly, so that hazard is gone.
/// What replaced it is bigger: the summary pane re-points its own DataContext, hosts a shared
/// <c>StoragePanelView</c> whose context is re-pointed AGAIN (to a nullable projection), and gates most of
/// itself on a selection that may be null. None of those is visible to the compiler, and each of them
/// renders nothing at all when wrong — a silently empty pane, which is exactly the failure a smoke test
/// has to catch.</para></summary>
[Collection(HeadlessCollection.Name)]
public sealed class JobQueueWindowSmokeTests(HeadlessSessionFixture headless)
{
    private static RunSummaryDto Run(
        string phase, string outcome = "None", bool paused = false, bool waiting = false,
        string name = "Photos", int planned = 12, int succeeded = 5, string? planError = null,
        Guid? runId = null, long bytesSettled = 0) =>
        new(runId ?? Guid.NewGuid(), Guid.NewGuid(), name, phase, outcome, paused, waiting,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            phase == "Closed" ? DateTimeOffset.UnixEpoch : null,
            planned, 2, 4096, 512, succeeded, 0, 0, 0, false, planError, bytesSettled);

    /// <summary>A plan summary with everything the pane can show turned ON — two volumes (one whose
    /// capacity could not be read), folder breakdowns, and non-zero counts in every blast-radius slot — so
    /// one layout pass covers every branch of the summary and the storage panel inside it.</summary>
    /// <remarks><paramref name="space"/> DEFAULTS to the sample projection when omitted — pass
    /// <c>with { Space = null }</c> to test the no-forecast branch, not <c>space: null</c>.</remarks>
    private static RunDetailDto Detail(Guid runId, SpaceProjection? space = null) => new(
        runId, ProfileFactory.Sample(), ScopePath: null, DateTimeOffset.UnixEpoch,
        CopyItemCount: 12, CopyBytes: 4096, DeleteItemCount: 2, DeleteBytes: 512,
        SourceItemCount: 40, DestinationItemCount: 14,
        OverwriteCount: 3, RenameCount: 1, DisposalCount: 12,
        Truncated: false, SweepFaultDetail: null, Space: space ?? SampleSpace());

    private static SpaceProjection SampleSpace() => new()
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
                RealisticPeakUsedBytes = 40_600, SafeCeilingUsedBytes = 41_000,
                Folders =
                [
                    new FolderSpaceBreakdown { Root = @"D:\a", BytesWrittenBytes = 4000, NetChangeBytes = 300, FileCount = 1 },
                ],
            },
            // Capacity unknown: the bar hides and a sentence replaces it, a branch the storage panel only
            // reaches for a volume it could not stat.
            new VolumeSpaceEstimate { VolumeRoot = "Z:", CapacityKnown = false, BytesWrittenBytes = 10, NetChangeBytes = 10 },
        ],
    };

    [Fact]
    public async Task The_queue_window_lays_out_every_row_shape()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                RunsResult = new List<RunSummaryDto>
                {
                    Run("Waiting", waiting: true, planned: 0, succeeded: 0),
                    Run("Planning", planned: 0, succeeded: 0),
                    Run("AwaitingApproval", succeeded: 0),
                    Run("Executing"),
                    Run("Executing", paused: true),
                    Run("Closed", "Succeeded"),
                    Run("Closed", "CompletedWithProblems"),
                    Run("Closed", "Cancelled"),
                    Run("Closed", "PlanFailed", planError: "the source folder is not available"),
                    Run("SomethingFromTheFuture"),   // an unrecognized phase must render, not throw
                    // Byte progress: a determinate bar with its own caption, which is a different branch of
                    // the row template from the file bar every other executing row above uses.
                    Run("Executing", bytesSettled: 2048),
                },
            };
            // Half the parked rows approvable and half not, so BOTH footer arms are laid out: Approve and
            // View plan live in the summary footer behind opposite gates.
            bool own = true;
            JobQueueViewModel vm = new(gateway) { IsOwnRun = _ => own = !own };
            await vm.ReconcileAsync();
            Assert.Equal(11, vm.Runs.Count);

            Window window = new JobQueueWindow { DataContext = vm };
            window.Show();
            window.Measure(new Avalonia.Size(1040, 640));
            window.Arrange(new Avalonia.Rect(0, 0, 1040, 640));
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>The summary pane with a plan on screen — the branch that carries almost all the new markup:
    /// the chip strip, the shared storage panel under a twice-re-pointed DataContext, the source/target
    /// lists, the read-only settings grid, and the footer's Approve arm.</summary>
    [Fact]
    public async Task The_summary_pane_lays_out_a_selected_runs_plan()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            Guid runId = Guid.NewGuid();
            FakeIpcGateway gateway = new()
            {
                RunsResult = new List<RunSummaryDto> { Run("AwaitingApproval", succeeded: 0, runId: runId) },
            };
            gateway.RunDetailResults[runId] = Detail(runId);

            // Own run, so the footer offers Approve rather than View plan.
            JobQueueViewModel vm = new(gateway) { IsOwnRun = _ => true };
            await vm.ReconcileAsync();
            vm.SelectedRun = vm.Runs[0];
            // The load is fire-and-forget from the property setter, so let it settle before laying out —
            // otherwise this measures the loading branch and proves nothing about the plan branch.
            await WaitForPlanAsync(vm);
            Assert.NotNull(vm.Summary.Plan);
            Assert.NotNull(vm.Summary.Plan!.Space);

            Window window = new JobQueueWindow { DataContext = vm };
            window.Show();
            window.Measure(new Avalonia.Size(1040, 640));
            window.Arrange(new Avalonia.Rect(0, 0, 1040, 640));
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>The two summary states that are NOT a plan: a run still planning (no snapshot header yet —
    /// the normal answer, not an error) and a truncated plan with no space projection, which is the one case
    /// where the storage panel's DataContext is null and its host has to say so instead of leaving a
    /// gap.</summary>
    [Fact]
    public async Task The_summary_pane_lays_out_its_no_plan_and_no_forecast_states()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            Guid planning = Guid.NewGuid();
            Guid truncated = Guid.NewGuid();
            FakeIpcGateway gateway = new()
            {
                RunsResult = new List<RunSummaryDto>
                {
                    Run("Planning", planned: 0, succeeded: 0, runId: planning),
                    Run("AwaitingApproval", succeeded: 0, runId: truncated),
                },
            };
            // planning falls through to RunDetailResult, which defaults to RUN_PLAN_UNAVAILABLE.
            // Space nulled through `with`, not through Detail's parameter: that parameter defaults to the
            // sample projection when omitted, so passing null there would silently give the rich one back.
            gateway.RunDetailResults[truncated] = Detail(truncated) with
            {
                Truncated = true,
                Space = null,
                SweepFaultDetail = @"could not read D:\x",
            };

            JobQueueViewModel vm = new(gateway);
            await vm.ReconcileAsync();

            JobQueueWindow window = new() { DataContext = vm };
            window.Show();

            vm.SelectedRun = vm.Runs.First(r => r.RunId == planning);
            await WaitForAsync(() => vm.Summary.HasNoPlanYet);
            window.Measure(new Avalonia.Size(1040, 640));
            window.Arrange(new Avalonia.Rect(0, 0, 1040, 640));

            vm.SelectedRun = vm.Runs.First(r => r.RunId == truncated);
            await WaitForPlanAsync(vm);
            Assert.False(vm.Summary.Plan!.HasSpace);
            window.Measure(new Avalonia.Size(1040, 640));
            window.Arrange(new Avalonia.Rect(0, 0, 1040, 640));

            window.Close();
        }, CancellationToken.None);
    }

    // ── Layout geometry ─────────────────────────────────────────────────────────────────────────────
    // A Measure/Arrange pass that does not THROW proves very little about a two-pane window: a footer that
    // sits over the content, a placeholder pinned to one side, and a splitter nowhere near the seam it is
    // supposed to be on all lay out perfectly happily. These assert the arranged rectangles, which is the
    // only way those three are visible to a test at all.

    /// <summary>The summary footer must never overlap the content above it. It did: the pane was a DockPanel
    /// whose non-last children docked to the side, so the scroller was measured against the wrong rectangle
    /// and the footer came to sit over the bottom of the card stack.</summary>
    [Fact]
    public async Task The_summary_footer_sits_below_the_content_rather_than_over_it()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            Guid runId = Guid.NewGuid();
            FakeIpcGateway gateway = new()
            {
                RunsResult = new List<RunSummaryDto> { Run("AwaitingApproval", succeeded: 0, runId: runId) },
            };
            gateway.RunDetailResults[runId] = Detail(runId);
            JobQueueViewModel vm = new(gateway) { IsOwnRun = _ => true };
            await vm.ReconcileAsync();
            vm.SelectedRun = vm.Runs[0];
            await WaitForPlanAsync(vm);

            JobQueueWindow window = Laid(vm);

            Border footer = Find<Border>(window, "SummaryFooter");
            ScrollViewer scroller = Find<ScrollViewer>(window, "SummaryScroller");
            Assert.True(footer.Bounds.Height > 0, "the footer must be laid out for this to mean anything");
            Assert.True(scroller.Bounds.Height > 0);

            // Both are children of the same two-row grid, so their Bounds share a coordinate space.
            Assert.True(footer.Bounds.Top >= scroller.Bounds.Bottom,
                $"the footer (top {footer.Bounds.Top}) overlaps the content (bottom {scroller.Bounds.Bottom})");

            // ...and the pane as a whole stays INSIDE its column. This is the other half of the same
            // symptom and the half an Arrange pass will otherwise wave through: the host used to carry a
            // negative bottom margin to reach the frame, which let it overflow its cell and draw its own
            // footer down across the window footer below. Arrange does not object to an overflowing child —
            // only the render clips — so nothing but an explicit containment check sees it.
            Control host = Find<Border>(window, "SummaryPaneHost");
            Grid split = Find<Grid>(window, "SplitRoot");
            Assert.True(host.Bounds.Bottom <= split.Bounds.Height + 0.5,
                $"the summary pane ends at {host.Bounds.Bottom} inside a {split.Bounds.Height}px row");
            Assert.True(host.Bounds.Top >= -0.5, $"the summary pane starts at {host.Bounds.Top}");
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>The splitter must BE the seam between the panes, not sit somewhere near it. It used to be a
    /// transparent column flush against the list, with the visible rule a dozen pixels further right — so
    /// the thing that looked like the divider was not the thing that resized.</summary>
    [Fact]
    public async Task The_splitter_is_the_seam_between_the_two_panes()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            JobQueueViewModel vm = new(new FakeIpcGateway());
            await vm.ReconcileAsync();

            JobQueueWindow window = Laid(vm);

            GridSplitter splitter = Find<GridSplitter>(window, "PaneSplitter");
            Border summary = Find<Border>(window, "SummaryPaneHost");
            Assert.True(splitter.Bounds.Width >= 4, "a hairline nobody can grab is the bug being fixed");
            // Flush on the right: no gap between the handle and the pane it resizes.
            Assert.Equal(splitter.Bounds.Right, summary.Bounds.Left, precision: 1);
            // And carries the class that paints it, so it can be SEEN rather than found by sweeping the
            // cursor for a resize arrow. Asserted as the class and not as the resolved Background: this
            // fixture's app merges no resource dictionaries, so every DynamicResource in the window
            // resolves to null here. ResourceKeyContractTests is what proves the brush itself exists.
            Assert.Contains("pane", splitter.Classes);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>An empty queue shows a placeholder ROW where the first row would be — not a full-pane empty
    /// state that changes the pane's shape, and not a header paragraph above it. Both used to be here.</summary>
    [Fact]
    public async Task An_empty_queue_shows_a_placeholder_row_and_still_shows_the_list()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            JobQueueViewModel vm = new(new FakeIpcGateway());
            await vm.ReconcileAsync();
            Assert.True(vm.HasNoRuns);

            JobQueueWindow window = Laid(vm);

            Grid empty = Find<Grid>(window, "EmptyQueueCard");
            Assert.True(empty.IsVisible);
            Assert.True(empty.Bounds.Height > 0);
            // At the TOP of the pane, where the first row would be — not centred over the whole pane.
            Grid pane = Find<Grid>(window, "QueuePane");
            Assert.Equal(0, empty.Bounds.Top, precision: 1);
            Assert.True(empty.Bounds.Width > pane.Bounds.Width - 2,
                "the placeholder should span the pane like a row does");

            // ...and the list is still a list. Its container survives an empty queue, so the pane does not
            // change shape when the first run arrives.
            Assert.True(Find<ListBox>(window).IsVisible);
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>With nothing selected the summary placeholder is centred on BOTH axes. It was centred
    /// vertically inside a block that filled the pane's width, so the text was centred and the block was
    /// not.</summary>
    [Fact]
    public async Task The_summary_placeholder_is_centred_on_both_axes()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                RunsResult = new List<RunSummaryDto> { Run("Executing") },
            };
            JobQueueViewModel vm = new(gateway);
            await vm.ReconcileAsync();
            Assert.Null(vm.SelectedRun);

            JobQueueWindow window = Laid(vm);

            StackPanel placeholder = Find<StackPanel>(window, "SummaryPlaceholder");
            ScrollViewer scroller = Find<ScrollViewer>(window, "SummaryScroller");
            Assert.True(placeholder.IsVisible);
            Assert.False(scroller.IsVisible);

            // Centred within its row: the slack above equals the slack below, and left equals right. Read
            // off the row's own height rather than the pane's, since the footer is hidden here.
            Rect row = placeholder.Bounds;
            Control host = Find<Border>(window, "SummaryPaneHost");
            double slackLeft = row.Left;
            double slackRight = host.Bounds.Width - row.Right;
            double slackTop = row.Top;
            double slackBottom = host.Bounds.Height - row.Bottom;
            // Within a pixel: an odd available width cannot be split into two equal halves, and Avalonia
            // rounds the remainder to one side. A tighter tolerance would fail on window sizes that are
            // perfectly centred.
            Assert.True(Math.Abs(slackLeft - slackRight) <= 1,
                $"not horizontally centred: {slackLeft} left vs {slackRight} right");
            Assert.True(Math.Abs(slackTop - slackBottom) <= 1,
                $"not vertically centred: {slackTop} above vs {slackBottom} below");
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>Nothing in the summary may be arranged past the pane's right edge. The pane narrows to its
    /// 360px minimum, and Avalonia's Fluent scrollbar floats over the content reserving no width — so a
    /// symmetric inset put the bar on top of every card's right edge, which read as the pane clipping
    /// itself.</summary>
    [Fact]
    public async Task The_summary_content_stays_inside_the_pane_at_its_narrowest()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            Guid runId = Guid.NewGuid();
            FakeIpcGateway gateway = new()
            {
                RunsResult = new List<RunSummaryDto> { Run("AwaitingApproval", succeeded: 0, runId: runId) },
            };
            gateway.RunDetailResults[runId] = Detail(runId);
            JobQueueViewModel vm = new(gateway) { IsOwnRun = _ => true };
            await vm.ReconcileAsync();
            vm.SelectedRun = vm.Runs[0];
            await WaitForPlanAsync(vm);

            // The window's own minimum, which drives both panes to theirs.
            JobQueueWindow window = Laid(vm, width: 840, height: 380);

            ScrollViewer scroller = Find<ScrollViewer>(window, "SummaryScroller");
            Control content = Assert.IsAssignableFrom<Control>(scroller.Content);
            // A gutter wide enough for the floating scrollbar has to remain to the content's right.
            double gutter = scroller.Bounds.Width - (content.Bounds.Right + scroller.Padding.Left);
            Assert.True(gutter >= 4,
                $"only {gutter}px is left for the overlay scrollbar; content will clip");
            window.Close();
        }, CancellationToken.None);
    }

    private static JobQueueWindow Laid(JobQueueViewModel vm, double width = 1040, double height = 640)
    {
        JobQueueWindow window = new() { DataContext = vm };
        window.Show();
        window.Measure(new Avalonia.Size(width, height));
        window.Arrange(new Avalonia.Rect(0, 0, width, height));
        // A second pass: the first settles the pane widths the splitter's columns resolve to, and the
        // content inside them is measured against those.
        window.Measure(new Avalonia.Size(width, height));
        window.Arrange(new Avalonia.Rect(0, 0, width, height));
        window.UpdateLayout();
        return window;
    }

    private static T Find<T>(JobQueueWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static T Find<T>(JobQueueWindow window) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First();

    private static Task WaitForPlanAsync(JobQueueViewModel vm) => WaitForAsync(() => vm.Summary.HasPlan);

    /// <summary>Yields until a fire-and-forget load has landed. Yielding rather than delaying: the fake
    /// gateway answers synchronously, so the continuation is already queued on this same dispatcher and one
    /// turn is normally enough — the loop is only a bound against a future fake that awaits.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Yield();
        Assert.True(condition(), "the summary never reached the expected state");
    }

    [Fact]
    public async Task The_queue_window_lays_out_its_empty_state()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            JobQueueViewModel vm = new(new FakeIpcGateway());
            await vm.ReconcileAsync();
            Assert.True(vm.HasNoRuns);

            Window window = new JobQueueWindow { DataContext = vm };
            window.Show();
            window.Measure(new Avalonia.Size(720, 520));
            window.Arrange(new Avalonia.Rect(0, 0, 720, 520));
            window.Close();
        }, CancellationToken.None);
    }

    /// <summary>The custom chrome, which is the whole reason this window is not a plain dialog frame: the
    /// OS keeps the resizable border but draws no system caption, so the bar follows the app theme. Same
    /// recipe as <c>SettingsWindow</c>, and the same failure mode if it regresses — the window silently
    /// falls back to a Windows-default caption that ignores the theme.
    ///
    /// <para>The footer close button is checked by name because it is icon-only: with no content to match
    /// on, nothing else would notice it disappearing.</para></summary>
    [Fact]
    public async Task The_queue_window_uses_the_apps_own_chrome_and_a_footer()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            JobQueueViewModel vm = new(new FakeIpcGateway());
            await vm.ReconcileAsync();

            JobQueueWindow window = new() { DataContext = vm };
            window.Show();
            window.Measure(new Avalonia.Size(720, 520));
            window.Arrange(new Avalonia.Rect(0, 0, 720, 520));

            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
            Assert.NotNull(window.FindControl<Button>("FooterCloseButton"));

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_queue_window_lays_out_its_error_bar()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                RunsResult = new IpcError("SERVICE_UNAVAILABLE", "the service is not running"),
            };
            JobQueueViewModel vm = new(gateway);
            await vm.ReconcileAsync();
            Assert.NotNull(vm.ErrorMessage);

            Window window = new JobQueueWindow { DataContext = vm };
            window.Show();
            window.Measure(new Avalonia.Size(720, 520));
            window.Arrange(new Avalonia.Rect(0, 0, 720, 520));
            window.Close();
        }, CancellationToken.None);
    }
}
