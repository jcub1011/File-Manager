using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Where Ctrl+Z is live, through the real window. The gesture is handled at window scope so it
/// works with focus in the sidebar, which means the ONLY thing keeping it off the Preview tab is the gate
/// in <c>MainWindow.OnUndoRedoKeyDown</c> — so that gate gets a test rather than a comment.
///
/// Deliberately never calls <c>Close()</c>: MainWindow's OnClosing is a two-pass async teardown that
/// cancels the first pass and shuts the service down, which is not what a smoke test wants to exercise.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class MainWindowUndoScopeTests(HeadlessSessionFixture headless)
{
    private static (MainWindow Window, MainWindowViewModel Shell) Show()
    {
        MainWindowViewModel shell = new(
            new FakeIpcGateway(), new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
            clientSettingsPath: Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json"));
        MainWindow window = new() { Width = 1200, Height = 800, DataContext = shell };
        window.Show();
        window.UpdateLayout();
        return (window, shell);
    }

    /// <summary>Focuses a control OUTSIDE the profile editor — the whole point of handling the gesture at
    /// window scope is that undo still works from here.</summary>
    private static void FocusOutsideTheEditor(MainWindow window)
    {
        Control outside = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.FindAncestorOfType<ProfileEditorView>() is null && b.IsEffectivelyVisible);
        outside.Focus();
    }

    /// <summary>Finds the text box bound to the draft's name.</summary>
    private static TextBox NameBox(MainWindow window) =>
        window.GetVisualDescendants().OfType<ProfileEditorView>().Single()
            .GetVisualDescendants().OfType<TextBox>()
            .First(t => !t.AcceptsReturn && t.Text is not null);

    [Fact]
    public async Task Ctrl_Z_in_a_text_box_does_not_toggle_forever()
    {
        // Once the history is empty, Ctrl+Z must stay a no-op. If the surface lets the key fall through to
        // the focused TextBox, the control's OWN undo stack takes over — and it still holds the
        // programmatic write our restore made, so undoing that re-applies the edit and re-enters our
        // history as a fresh step. Pressing Ctrl+Z would then flip between the two values forever instead
        // of walking back.
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, shell) = Show();
            try
            {
                shell.Editor.Load(ProfileFactory.Sample());
                window.UpdateLayout();

                TextBox name = NameBox(window);
                name.Focus();
                name.Text = "renamed";                      // as typing does, through the two-way binding
                Assert.Equal("renamed", shell.Editor.ProfileName);

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
                Assert.Equal("Existing", shell.Editor.ProfileName);
                Assert.False(shell.Editor.IsDirty);

                // The press that used to bring the edit back.
                for (int i = 0; i < 4; i++)
                {
                    window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
                    Assert.Equal("Existing", shell.Editor.ProfileName);
                    Assert.False(shell.Editor.IsDirty);
                    Assert.False(shell.Editor.History.CanUndo);
                }
            }
            finally { window.Hide(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Ctrl_Z_undoes_the_draft_with_focus_outside_the_editor()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, shell) = Show();
            try
            {
                shell.Editor.Load(ProfileFactory.Sample());
                window.UpdateLayout();
                shell.Editor.ProfileName = "renamed";
                FocusOutsideTheEditor(window);

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

                Assert.Equal("Existing", shell.Editor.ProfileName);
                Assert.False(shell.Editor.IsDirty);
            }
            finally { window.Hide(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Ctrl_Z_on_the_preview_tab_leaves_the_profile_draft_alone()
    {
        // The reported worry, pinned: stepping the profile backwards from a tab that shows a run preview
        // would be an edit the user cannot see happening.
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, shell) = Show();
            try
            {
                shell.NewProfile();                       // also makes the Preview tab reachable
                shell.Editor.ProfileName = "renamed";
                window.UpdateLayout();

                TabItem preview = Assert.IsType<TabItem>(window.FindControl<TabItem>("PreviewTab"));
                TabItem profile = Assert.IsType<TabItem>(window.FindControl<TabItem>("ProfileTab"));
                preview.IsSelected = true;
                window.UpdateLayout();
                // Assert the switch actually took, or the rest of this test proves nothing.
                Assert.True(preview.IsSelected);
                Assert.False(profile.IsSelected);

                // And focus something, or no KeyDown is raised at all and the test passes vacuously.
                FocusOutsideTheEditor(window);
                int depthBefore = shell.Editor.History.UndoDepth;
                Assert.True(depthBefore > 0);            // there IS something that could be undone

                // Assert after EACH press. Undo followed by redo round-trips, so checking only at the end
                // cannot tell "the gate held" from "both keys fired".
                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
                Assert.Equal("renamed", shell.Editor.ProfileName);
                Assert.Equal(depthBefore, shell.Editor.History.UndoDepth);

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Equal("renamed", shell.Editor.ProfileName);
                Assert.Equal(depthBefore, shell.Editor.History.UndoDepth);

                window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
                Assert.Equal("renamed", shell.Editor.ProfileName);
                Assert.Equal(depthBefore, shell.Editor.History.UndoDepth);
                Assert.True(shell.Editor.IsDirty);
            }
            finally { window.Hide(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Selecting_the_profile_tab_again_makes_undo_live_again()
    {
        // The gate must be a gate, not a one-way latch.
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, shell) = Show();
            try
            {
                shell.NewProfile();
                shell.Editor.ProfileName = "renamed";
                window.UpdateLayout();

                TabItem preview = Assert.IsType<TabItem>(window.FindControl<TabItem>("PreviewTab"));
                TabItem profile = Assert.IsType<TabItem>(window.FindControl<TabItem>("ProfileTab"));
                preview.IsSelected = true;
                window.UpdateLayout();
                profile.IsSelected = true;
                window.UpdateLayout();
                FocusOutsideTheEditor(window);

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

                Assert.Equal("New Profile", shell.Editor.ProfileName);
            }
            finally { window.Hide(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>Belt-and-braces, and honestly so: with no profile loaded the history has already been
    /// reset, so the CanExecute check would refuse the command anyway. This pins the observable behaviour
    /// (an empty editor swallows nothing and changes nothing) rather than the redundant guard.</summary>
    [Fact]
    public async Task Ctrl_Z_with_no_profile_loaded_is_inert()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, shell) = Show();
            try
            {
                Assert.False(shell.Editor.HasProfile);
                FocusOutsideTheEditor(window);

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

                Assert.False(shell.Editor.IsDirty);
                Assert.False(shell.Editor.History.CanUndo);
            }
            finally { window.Hide(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }
}
