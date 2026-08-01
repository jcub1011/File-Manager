using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>"Run now" starts a REAL run that moves files, so these pin the guard rails: the
/// confirmation, the dirty-editor refusal, and one request per source root.</summary>
public sealed class MainWindowViewModelRunProfileTests
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

    private static (MainWindowViewModel Shell, FakeIpcGateway Gateway) NewShell(
        Profile? profile = null, Func<string, Task<bool>>? confirm = null)
    {
        FakeIpcGateway gateway = new() { GetResult = profile ?? TwoSourceProfile() };
        MainWindowViewModel shell = new(
            gateway, new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"))
        {
            ConfirmRunProfile = confirm ?? (_ => Task.FromResult(true)),
        };
        return (shell, gateway);
    }

    [Fact]
    public async Task Run_now_sends_one_request_per_source_root()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();

        await shell.RunProfileNowAsync(Row());

        Assert.Equal([(ProfileId, @"C:\in\a"), (ProfileId, @"C:\in\b")], gateway.RunProfileCalls);
        Assert.Null(shell.List.ErrorMessage);
    }

    [Fact]
    public async Task Run_now_opens_the_activity_panel()
    {
        (MainWindowViewModel shell, _) = NewShell();

        await shell.RunProfileNowAsync(Row());

        Assert.True(shell.ActivityVisible);
    }

    [Fact]
    public async Task Declining_the_confirmation_sends_nothing()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell(confirm: _ => Task.FromResult(false));

        await shell.RunProfileNowAsync(Row());

        Assert.Empty(gateway.RunProfileCalls);
        Assert.False(shell.ActivityVisible);
    }

    [Fact]
    public async Task The_confirmation_names_the_source_roots_and_the_disposition()
    {
        // This dialog is the ONLY place the user sees the blast radius, so its content is pinned.
        string? shown = null;
        Profile profile = TwoSourceProfile();
        profile = profile with { Policies = profile.Policies with { OnSuccess = OnSuccessAction.MoveToTrash } };
        (MainWindowViewModel shell, _) = NewShell(profile, confirm: message =>
        {
            shown = message;
            return Task.FromResult(false);
        });

        await shell.RunProfileNowAsync(Row());

        Assert.Contains("Photos", shown);
        Assert.Contains(@"C:\in\a", shown);
        Assert.Contains(@"C:\in\b", shown);
        Assert.Contains("RECYCLE BIN", shown);
    }

    [Fact]
    public async Task A_null_confirm_callback_proceeds()
    {
        // Headless contract, matching ConfirmImport's documented behaviour.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        shell.ConfirmRunProfile = null;

        await shell.RunProfileNowAsync(Row());

        Assert.Equal(2, gateway.RunProfileCalls.Count);
    }

    [Fact]
    public async Task Run_now_is_refused_while_the_editor_has_unsaved_edits_for_that_profile()
    {
        // run-profile resolves against the PERSISTED catalog, so unsaved edits would be silently
        // ignored — a real "why didn't my change apply?" trap. Set the state up the way the app does:
        // select the row (which loads it into the editor), then edit it, letting the shell's own
        // Editor.PropertyChanged wiring point UnsavedProfileId at the selected profile.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        ProfileListItem row = Row();
        shell.List.Profiles.Add(row);
        shell.List.SelectedProfile = row;
        await WaitUntilAsync(() => shell.Editor.HasProfile);

        shell.Editor.IncludeGlobsText = "*.zzz";     // any edit marks the draft dirty
        Assert.True(shell.Editor.IsDirty);
        Assert.Equal(ProfileId, shell.List.UnsavedProfileId);

        await shell.RunProfileNowAsync(row);

        Assert.Empty(gateway.RunProfileCalls);
        Assert.True(shell.Editor.ShowUnsavedWarning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }

    [Fact]
    public async Task Per_source_failures_aggregate_into_the_list_banner()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        gateway.RunProfileResults[@"C:\in\b"] = new IpcError("PATH_NOT_FOUND", "path does not exist");

        await shell.RunProfileNowAsync(Row());

        Assert.Contains("Started 1 of 2", shell.List.ErrorMessage);
        Assert.Contains(@"C:\in\b", shell.List.ErrorMessage);
    }

    [Fact]
    public async Task An_inactive_profile_refusal_lands_in_the_banner()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        gateway.RunProfileResult = new IpcError("PROFILE_INACTIVE", "profile \"Photos\" is inactive");

        await shell.RunProfileNowAsync(Row());

        Assert.Contains("inactive", shell.List.ErrorMessage);
    }

    [Fact]
    public async Task A_profile_load_failure_is_reported_and_nothing_is_run()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        gateway.GetResult = new IpcError("PROFILE_NOT_FOUND", "gone");

        await shell.RunProfileNowAsync(Row());

        Assert.Empty(gateway.RunProfileCalls);
        Assert.Contains("gone", shell.List.ErrorMessage);
    }

    [Fact]
    public async Task A_profile_with_no_sources_is_reported_and_nothing_is_run()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell(TwoSourceProfile() with { Sources = [] });

        await shell.RunProfileNowAsync(Row());

        Assert.Empty(gateway.RunProfileCalls);
        Assert.Contains("no sources", shell.List.ErrorMessage);
    }

    [Fact]
    public async Task A_null_row_is_a_no_op()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();

        await shell.RunProfileNowAsync(null);

        Assert.Empty(gateway.RunProfileCalls);
    }
}
