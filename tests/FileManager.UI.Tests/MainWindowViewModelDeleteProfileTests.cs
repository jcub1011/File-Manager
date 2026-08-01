using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>Delete is reachable from the Profile tab, a list row, and the collapsed rail, so the
/// confirmation is a modal owned by the shell rather than an inline bar in one view. These pin that
/// the modal is honoured and that the tab's parameterless press resolves the selected profile.</summary>
public sealed class MainWindowViewModelDeleteProfileTests
{
    private static readonly Guid IdA = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid IdB = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private static (MainWindowViewModel Shell, FakeIpcGateway Gateway) NewShell(
        Func<string, Task<bool>>? confirm = null)
    {
        FakeIpcGateway gateway = new()
        {
            ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
            [
                new ProfileSummary(IdA, "Alpha", true, "Manual"),
                new ProfileSummary(IdB, "Beta", true, "Manual"),
            ]),
        };
        MainWindowViewModel shell = new(
            gateway, new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"))
        {
            ConfirmDeleteProfile = confirm ?? (_ => Task.FromResult(true)),
        };
        return (shell, gateway);
    }

    [Fact]
    public async Task Confirming_deletes_the_named_profile()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        await shell.List.RefreshAsync();

        await shell.DeleteProfileAsync(shell.List.Profiles[1]);

        Assert.Equal([IdB], gateway.DeleteCalls);
    }

    [Fact]
    public async Task Declining_deletes_nothing()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell(confirm: _ => Task.FromResult(false));
        await shell.List.RefreshAsync();

        await shell.DeleteProfileAsync(shell.List.Profiles[0]);

        Assert.Empty(gateway.DeleteCalls);
    }

    [Fact]
    public async Task The_confirmation_names_the_profile()
    {
        string? shown = null;
        (MainWindowViewModel shell, _) = NewShell(confirm: message =>
        {
            shown = message;
            return Task.FromResult(false);
        });
        await shell.List.RefreshAsync();

        await shell.DeleteProfileAsync(shell.List.Profiles[0]);

        Assert.Contains("Alpha", shown);
    }

    [Fact]
    public async Task A_null_row_falls_back_to_the_selected_profile()
    {
        // How the Profile tab's button behaves if its CommandParameter ever arrives null.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        await shell.List.RefreshAsync();
        shell.List.SelectedProfile = shell.List.Profiles[1];

        await shell.DeleteProfileAsync(null);

        Assert.Equal([IdB], gateway.DeleteCalls);
    }

    [Fact]
    public async Task A_null_row_with_no_selection_is_a_no_op()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        await shell.List.RefreshAsync();

        await shell.DeleteProfileAsync(null);

        Assert.Empty(gateway.DeleteCalls);
    }

    [Fact]
    public async Task A_null_confirm_callback_proceeds()
    {
        // Headless contract, matching ConfirmRunProfile's documented behaviour.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        shell.ConfirmDeleteProfile = null;
        await shell.List.RefreshAsync();

        await shell.DeleteProfileAsync(shell.List.Profiles[0]);

        Assert.Equal([IdA], gateway.DeleteCalls);
    }

    [Fact]
    public async Task Deleting_a_profile_with_unsaved_edits_discards_the_draft_and_clears_the_editor()
    {
        // Unlike Run (which refuses a dirty editor because it resolves the persisted catalog), Delete
        // destroys the profile — so the draft goes with it instead of blocking the deselection.
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        gateway.GetResult = ProfileFactory.Sample(IdA);
        await shell.List.RefreshAsync();
        shell.List.SelectedProfile = shell.List.Profiles[0];
        await WaitUntilAsync(() => shell.Editor.HasProfile);
        shell.Editor.IncludeGlobsText = "*.zzz";
        Assert.Equal(IdA, shell.List.UnsavedProfileId);

        await shell.DeleteProfileAsync(shell.List.Profiles[0]);

        Assert.Equal([IdA], gateway.DeleteCalls);
        Assert.Null(shell.List.SelectedProfile);
        Assert.False(shell.Editor.IsDirty);
        await WaitUntilAsync(() => !shell.Editor.HasProfile);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "the awaited condition never became true");
    }

    [Fact]
    public async Task The_run_and_delete_buttons_gate_on_the_selected_row()
    {
        (MainWindowViewModel shell, _) = NewShell();
        Assert.False(shell.List.CanDeleteSelected);
        Assert.False(shell.List.CanRunSelected);

        await shell.List.RefreshAsync();
        shell.List.SelectedProfile = shell.List.Profiles[0];

        Assert.True(shell.List.CanDeleteSelected);
        Assert.True(shell.List.CanRunSelected);
    }

    [Fact]
    public async Task An_inactive_profile_can_be_deleted_but_not_run()
    {
        (MainWindowViewModel shell, FakeIpcGateway gateway) = NewShell();
        gateway.ListResult = Result<IReadOnlyList<ProfileSummary>, IpcError>.Success(
            [new ProfileSummary(IdA, "Alpha", false, "Manual")]);
        await shell.List.RefreshAsync();
        shell.List.SelectedProfile = shell.List.Profiles[0];

        Assert.True(shell.List.CanDeleteSelected);
        Assert.False(shell.List.CanRunSelected);
    }
}
