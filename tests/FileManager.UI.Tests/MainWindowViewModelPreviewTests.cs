using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>"Preview" starts the PLANNING phase of a real run and shows its frozen work list on the
/// Preview tab. Nothing is copied or deleted by any of this — the footer's Approve is what does that
/// (see <see cref="DryRunViewModelTests"/>) — so what these pin is what reaches the engine: one request
/// for the whole profile, carrying the editor's draft, and no dialog anywhere.
///
/// <para>The dialogs this replaced were two: a pre-flight blast-radius modal and an approval modal
/// quoting four counts. Both are gone deliberately. Planning is read-only by construction, so there is
/// nothing to confirm before it, and the tab now shows the rows the counts were standing in for.</para></summary>
public sealed class MainWindowViewModelPreviewTests
{
    private static readonly Guid ProfileId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static Profile TwoSourceProfile() => ProfileFactory.Sample(ProfileId) with
    {
        Name = "Photos",
        Transformers = [],          // transformers are refused by the engine in this slice
        Sources =
        [
            new SourceConfig { Path = @"C:\in\a" },
            new SourceConfig { Path = @"C:\in\b" },
        ],
    };

    private static ProfileListItem Row(bool active = true) => new(ProfileId, "Photos", active, "Manual");

    private static (MainWindowViewModel Shell, FakeIpcGateway Gateway) NewShell(Profile? profile = null)
    {
        FakeIpcGateway gateway = new() { GetResult = profile ?? TwoSourceProfile() };
        MainWindowViewModel shell = new(
            gateway, new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"));
        return (shell, gateway);
    }

    /// <summary>Puts the shell in the state the app is in when Preview is pressed: the row selected and
    /// its profile loaded into the editor, which is where the draft comes from.</summary>
    private static async Task<(MainWindowViewModel Shell, FakeIpcGateway Gateway)> OpenedShellAsync(
        Profile? profile = null)
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell(profile);
        ProfileListItem row = Row();
        shell.List.Profiles.Add(row);
        shell.List.SelectedProfile = row;
        await WaitUntilAsync(() => shell.Editor.HasProfile);
        return (shell, gateway);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }

    [Fact]
    public async Task Preview_sends_ONE_request_for_the_whole_profile()
    {
        // This used to be one request per source root, which is wrong for Mirror: an orphan is "a
        // destination file no source writes to", so the decision needs the COMPLETE source set. Two
        // independent runs would each see the other source's files as orphans.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();

        await shell.PreviewProfileAsync(Row());

        Assert.Equal([(ProfileId, (string?)null)], gateway.RunProfileCalls);
        Assert.Null(shell.List.ErrorMessage);
    }

    [Fact]
    public async Task Preview_plans_the_editors_DRAFT_not_the_persisted_profile()
    {
        // The whole reason run-profile gained InlineProfile: what the user is looking at is what gets
        // planned, and — because the draft is frozen into the run's snapshot — what gets executed.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();
        shell.Editor.IncludeGlobsText = "*.raw";      // an unsaved edit
        Assert.True(shell.Editor.IsDirty);

        await shell.PreviewProfileAsync(Row());

        Profile? draft = Assert.Single(gateway.RunProfileDrafts);
        Assert.NotNull(draft);
        Assert.NotNull(draft.Filters);
        Assert.Contains("*.raw", draft.Filters.Include!);
    }

    [Fact]
    public async Task Preview_navigates_to_the_Preview_tab()
    {
        (MainWindowViewModel shell, _) = await OpenedShellAsync();
        Assert.Equal(MainWindowViewModel.ProfileTabIndex, shell.SelectedTabIndex);

        await shell.PreviewProfileAsync(Row());

        Assert.Equal(MainWindowViewModel.PreviewTabIndex, shell.SelectedTabIndex);
    }

    [Fact]
    public async Task Preview_does_NOT_open_the_activity_panel()
    {
        // Nothing has happened yet. The panel opens when the user APPROVES the plan — that is when
        // there is a run to watch land.
        (MainWindowViewModel shell, _) = await OpenedShellAsync();

        await shell.PreviewProfileAsync(Row());

        Assert.False(shell.ActivityVisible);
    }

    [Fact]
    public async Task Preview_marks_the_tab_busy_until_the_plan_arrives()
    {
        // The planning scan happens service-side and reports nothing to the client, so without this the
        // Preview tab would sit blank and idle-looking for the whole scan.
        (MainWindowViewModel shell, _) = await OpenedShellAsync();

        await shell.PreviewProfileAsync(Row());

        Assert.True(shell.DryRun.IsPreviewing);
    }

    [Fact]
    public async Task The_planning_scan_is_cancellable_once_the_engine_has_accepted_the_run()
    {
        // Planning a large profile is minutes of walking. The old dry run could be cancelled mid-scan and
        // this must not lose that — nothing has been touched, so abandoning it is free.
        Guid runId = Guid.NewGuid();
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();
        gateway.RunProfileResult = new RunProfileResponse { RunId = runId };

        await shell.PreviewProfileAsync(Row());
        Assert.Equal(runId, shell.DryRun.PlanningRunId);

        await shell.DryRun.CancelPreviewCommand.ExecuteAsync(null);

        Assert.Equal(runId, Assert.Single(gateway.CancelRunCalls));
        Assert.False(shell.DryRun.IsPreviewing);
        Assert.Null(shell.DryRun.PlanningRunId);
        Assert.Contains("cancelled", shell.DryRun.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_draft_parse_error_is_reported_and_nothing_is_run()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();
        shell.Editor.MaxDepthText = "not a number";

        await shell.PreviewProfileAsync(Row());

        Assert.Empty(gateway.RunProfileCalls);
        Assert.NotNull(shell.List.ErrorMessage);
        // It must not have navigated: a Preview tab showing nothing is worse than staying put.
        Assert.Equal(MainWindowViewModel.ProfileTabIndex, shell.SelectedTabIndex);
    }

    [Fact]
    public async Task Previewing_ANOTHER_profile_is_refused_while_the_draft_is_dirty()
    {
        // The draft is the only thing a preview plans, so previewing a different row means opening it —
        // and that is exactly the navigation the unsaved-changes guard exists to refuse.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();
        shell.Editor.IncludeGlobsText = "*.zzz";
        Assert.True(shell.Editor.IsDirty);

        await shell.PreviewProfileAsync(new ProfileListItem(Guid.NewGuid(), "Other", true, "Manual"));

        Assert.Empty(gateway.RunProfileCalls);
        Assert.True(shell.Editor.ShowUnsavedWarning);
    }

    [Fact]
    public async Task A_refused_run_lands_in_the_preview_tab_and_ends_the_wait()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();
        gateway.RunProfileResult = new IpcError("RUN_NOT_STARTED", "the service is shutting down");

        await shell.PreviewProfileAsync(Row());

        Assert.Contains("shutting down", shell.DryRun.ErrorMessage);
        // Not left spinning: the run never existed, so there is nothing more to wait for.
        Assert.False(shell.DryRun.IsPreviewing);
    }

    [Fact]
    public async Task An_inactive_profile_refusal_is_reported()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = await OpenedShellAsync();
        gateway.RunProfileResult = new IpcError("PROFILE_INACTIVE", "profile \"Photos\" is inactive");

        await shell.PreviewProfileAsync(Row());

        Assert.Contains("inactive", shell.DryRun.ErrorMessage);
    }

    [Fact]
    public async Task A_profile_with_no_sources_is_reported_and_nothing_is_run()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) =
            await OpenedShellAsync(TwoSourceProfile() with { Sources = [] });

        await shell.PreviewProfileAsync(Row());

        Assert.Empty(gateway.RunProfileCalls);
        Assert.Contains("no sources", shell.List.ErrorMessage);
    }

    [Fact]
    public async Task A_null_row_previews_whatever_the_editor_already_holds()
    {
        // The Preview tab's own button passes the selected row, which is null for a never-saved draft —
        // and a never-saved profile is still previewable, because the draft goes inline.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        shell.Editor.Load(TwoSourceProfile());

        await shell.PreviewProfileAsync(null);

        Assert.Single(gateway.RunProfileCalls);
        Assert.Equal(MainWindowViewModel.PreviewTabIndex, shell.SelectedTabIndex);
    }
}
