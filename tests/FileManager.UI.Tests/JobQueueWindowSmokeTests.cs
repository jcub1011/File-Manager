using Avalonia.Controls;
using FileManager.Contracts.IPC;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real <see cref="JobQueueWindow"/> against a queue holding every row shape and forces
/// a layout pass, so runtime-only XAML failures — a bad <c>x:DataType</c>, an IconConverters typo, a
/// missing icon geometry, and above all the <c>$parent[ItemsControl]</c> command bindings the row buttons
/// depend on — fail here rather than in the app.
///
/// <para>Those parent bindings are the reason this test earns its keep: the commands live on the view
/// model while the buttons live in an item template, so nothing but a real layout pass proves they
/// resolve.</para></summary>
[Collection(HeadlessCollection.Name)]
public sealed class JobQueueWindowSmokeTests(HeadlessSessionFixture headless)
{
    private static RunSummaryDto Run(
        string phase, string outcome = "None", bool paused = false, bool waiting = false,
        string name = "Photos", int planned = 12, int succeeded = 5, string? planError = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, phase, outcome, paused, waiting,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            phase == "Closed" ? DateTimeOffset.UnixEpoch : null,
            planned, 2, 4096, 512, succeeded, 0, 0, 0, false, planError);

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
                },
            };
            // Half the parked rows approvable and half not, so BOTH footer arms are laid out: the Approve
            // button and the View plan button live in the same row template behind opposite gates.
            bool own = true;
            JobQueueViewModel vm = new(gateway) { IsOwnRun = _ => own = !own };
            await vm.ReconcileAsync();
            Assert.Equal(10, vm.Runs.Count);

            Window window = new JobQueueWindow { DataContext = vm };
            window.Show();
            window.Measure(new Avalonia.Size(720, 520));
            window.Arrange(new Avalonia.Rect(0, 0, 720, 520));
            window.Close();
        }, CancellationToken.None);
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
