using Avalonia.Controls;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real ActivityView and StatusBarView against populated view models and forces a
/// layout pass, so runtime-only XAML failures — an IconConverters typo, a bad x:DataType, the new
/// Button.statusBar style, a missing icon geometry — fail here rather than in the app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class ActivityViewSmokeTests(HeadlessSessionFixture headless)
{
    private static readonly Guid ProfileId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private static JobSummaryDto Summary(string outcome, string? skipReason, string name) =>
        new(Guid.NewGuid(), ProfileId, $@"C:\src\{name}", outcome, skipReason,
            DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(1500));

    [Fact]
    public async Task Activity_view_loads_and_lays_out_every_outcome_kind()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                RecentJobsResult = Result<IReadOnlyList<JobSummaryDto>, IpcError>.Success(
                [
                    Summary("Succeeded", null, "ok.txt"),
                    Summary("Skipped", "Filtered", "filtered.txt"),
                    Summary("Skipped", "UnchangedAtAllTargets", "same.txt"),
                    Summary("Failed", null, "boom.txt"),
                    Summary("RollbackFailed", null, "residual.txt"),
                    Summary("SomethingUnknown", null, "future.txt"),
                ]),
                JobLogResult = Result<IReadOnlyList<string>, IpcError>.Success(
                    ["opened", "target 0: placed at C:\\dst\\ok.txt", "committed", "succeeded"]),
            };
            ActivityViewModel vm = new(gateway) { ProfileNameLookup = _ => "Photos" };
            await vm.ReconcileAsync();

            // A running row with live progress, plus a residual-paths row — both templated differently.
            vm.OnJobStarted(new JobStartedEvent
            {
                AtUtc = DateTimeOffset.UnixEpoch,
                JobId = Guid.NewGuid(),
                ProfileId = ProfileId,
                SourcePath = @"C:\src\live.txt",
            });
            vm.Jobs[0].ProgressText = "Distributing · 1/2 targets";
            vm.Jobs[^1].ResidualPaths = [@"C:\left\behind.tmp"];
            vm.SelectedJob = vm.Jobs[^1];

            Window window = new() { Width = 900, Height = 300, Content = new ActivityView { DataContext = vm } };
            window.Show();
            window.Measure(new Avalonia.Size(900, 300));
            window.Arrange(new Avalonia.Rect(0, 0, 900, 300));
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Activity_view_loads_in_its_empty_state()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            ActivityViewModel vm = new(new FakeIpcGateway());
            await vm.ReconcileAsync();
            Assert.False(vm.HasJobs);

            Window window = new() { Width = 900, Height = 300, Content = new ActivityView { DataContext = vm } };
            window.Show();
            window.Measure(new Avalonia.Size(900, 300));
            window.Arrange(new Avalonia.Rect(0, 0, 900, 300));
            window.Close();
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_bar_loads_with_the_pause_toggle(bool paused)
    {
        await headless.Session.DispatchAsync(async () =>
        {
            FakeIpcGateway gateway = new()
            {
                StatusResult = new EngineStatusSnapshot(paused, 2, 1, 0, null),
            };
            StatusBarViewModel vm = new(gateway);
            await vm.PollOnceAsync();
            Assert.Equal(paused, vm.IsPaused);

            Window window = new() { Width = 600, Height = 40, Content = new StatusBarView { DataContext = vm } };
            window.Show();
            window.Measure(new Avalonia.Size(600, 40));
            window.Arrange(new Avalonia.Rect(0, 0, 600, 40));
            window.Close();
        }, CancellationToken.None);
    }
}
