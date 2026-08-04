using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

/// <summary>The shell's half of the editor's undo session: the sidebar marker and navigation lock it
/// drives, and — the important part — that undo can never reach a profile other than the one loaded.
/// <see cref="ProfileEditorUndoTests"/> pins the same isolation at the view-model level; these run it
/// through the real selection path, which is what actually switches profiles in the app.</summary>
public sealed class MainWindowViewModelEditorUndoTests
{
    private static readonly Guid IdA = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid IdB = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private static (MainWindowViewModel Shell, FakeIpcGateway Gateway) NewShell()
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
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"));
        return (shell, gateway);
    }

    /// <summary>Drives the real selection path: scripts the gateway with the profile that row resolves to,
    /// then moves the selection the way a click does.</summary>
    private static async Task SelectAsync(
        MainWindowViewModel shell, FakeIpcGateway gateway, int row, Guid id, string name)
    {
        gateway.GetResult = ProfileFactory.Sample(id) with { Name = name };
        shell.List.SelectedProfile = shell.List.Profiles[row];
        await Task.Yield();
    }

    // ============================ The sidebar marker ============================

    [Fact]
    public async Task An_editor_edit_raises_the_sidebar_unsaved_marker()
    {
        var (shell, gateway) = NewShell();
        await shell.List.RefreshAsync();
        await SelectAsync(shell, gateway, 0, IdA, "Alpha");

        shell.Editor.ProfileName = "renamed";

        Assert.True(shell.List.HasUnsavedChanges);
        Assert.Equal(IdA, shell.List.UnsavedProfileId);
    }

    [Fact]
    public void Undoing_the_last_edit_clears_the_marker_and_unlocks_navigation()
    {
        var (shell, _) = NewShell();
        shell.Editor.Load(ProfileFactory.Sample());
        shell.Editor.ProfileName = "renamed";
        Assert.False(shell.List.CanNavigate!());

        shell.Editor.History.UndoCommand.Execute(null);

        Assert.False(shell.List.HasUnsavedChanges);
        Assert.Null(shell.List.UnsavedProfileId);
        Assert.True(shell.List.CanNavigate!());
    }

    [Fact]
    public void Undo_keeps_the_preview_footers_Mirror_warning_in_step_with_the_restored_sync_mode()
    {
        // Undo restores through the property setters, so the shell's SyncMode subscription fires the same
        // way a user edit does — and the footer must not keep warning about deletions the profile no
        // longer does.
        var (shell, _) = NewShell();
        shell.Editor.LoadNew();
        shell.Editor.SyncMode = SyncMode.Mirror;
        Assert.Contains("MIRROR", shell.DryRun.MirrorWarning);

        shell.Editor.History.UndoCommand.Execute(null);

        Assert.Equal("", shell.DryRun.MirrorWarning);
    }

    // ============================ Confined to the selected profile ============================

    [Fact]
    public async Task Selecting_another_profile_is_blocked_while_the_draft_is_dirty()
    {
        // The first line of defence: while there are unsaved edits the selection never moves, so a live
        // history is never even adjacent to a different profile.
        var (shell, gateway) = NewShell();
        await shell.List.RefreshAsync();
        await SelectAsync(shell, gateway, 0, IdA, "Alpha");
        shell.Editor.ProfileName = "edited";

        await SelectAsync(shell, gateway, 1, IdB, "Beta");

        Assert.Same(shell.List.Profiles[0], shell.List.SelectedProfile);   // the move was reverted
        Assert.True(shell.Editor.ShowUnsavedWarning);
        Assert.Equal("edited", shell.Editor.ProfileName);                  // draft untouched
    }

    [Fact]
    public async Task Undoing_back_to_clean_unlocks_navigation_and_the_next_profile_starts_fresh()
    {
        // The sharp case, end to end. Undoing to clean is what unlocks the sidebar, and the redo branch is
        // still live at that moment — so if the load did not drop it, one Ctrl+Y after switching would
        // apply Alpha's edit to Beta.
        var (shell, gateway) = NewShell();
        await shell.List.RefreshAsync();
        await SelectAsync(shell, gateway, 0, IdA, "Alpha");
        shell.Editor.ProfileName = "edited";
        shell.Editor.History.UndoCommand.Execute(null);
        Assert.False(shell.Editor.IsDirty);
        Assert.True(shell.Editor.History.CanRedo);

        await SelectAsync(shell, gateway, 1, IdB, "Beta");

        Assert.Equal("Beta", shell.Editor.ProfileName);
        Assert.False(shell.Editor.History.CanUndo);
        Assert.False(shell.Editor.History.CanRedo);

        shell.Editor.History.RedoCommand.Execute(null);
        Assert.Equal("Beta", shell.Editor.ProfileName);
    }

    [Fact]
    public async Task A_save_through_the_shell_keeps_the_history_steppable()
    {
        // AfterSaveAsync refreshes and re-selects under _revertingSelection, so SelectionCommitted never
        // fires and the draft is deliberately NOT re-loaded — which is what lets the user step back
        // through what they just saved. If that ever becomes a real re-load, this fails rather than the
        // post-save undo silently disappearing.
        var (shell, gateway) = NewShell();
        await shell.List.RefreshAsync();
        await SelectAsync(shell, gateway, 0, IdA, "Alpha");
        shell.Editor.ProfileName = "renamed";

        await shell.Editor.SaveCommand.ExecuteAsync(null);
        await Task.Yield();

        Assert.False(shell.Editor.IsDirty);
        Assert.True(shell.Editor.History.CanUndo);
        Assert.Equal("renamed", shell.Editor.ProfileName);
    }

    [Fact]
    public async Task Deleting_the_open_profile_leaves_nothing_to_undo()
    {
        var (shell, gateway) = NewShell();
        shell.ConfirmDeleteProfile = _ => Task.FromResult(true);
        await shell.List.RefreshAsync();
        await SelectAsync(shell, gateway, 0, IdA, "Alpha");
        shell.Editor.ProfileName = "edited";

        await shell.DeleteProfileAsync(shell.List.Profiles[0]);

        Assert.False(shell.Editor.HasProfile);
        Assert.False(shell.Editor.History.CanUndo);
        Assert.False(shell.Editor.History.CanRedo);
    }
}
