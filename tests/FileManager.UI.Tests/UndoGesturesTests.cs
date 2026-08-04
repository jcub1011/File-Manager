using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using FileManager.UI.Undo;

namespace FileManager.UI.Tests;

/// <summary>The keyboard half of undo/redo, in isolation from any particular editing surface. Both the
/// settings dialog and the profile tab route their key events through <see cref="UndoGestures"/>, so the
/// gesture set and the "did it claim the key?" contract are pinned once here rather than per surface.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class UndoGesturesTests(HeadlessSessionFixture headless)
{
    /// <summary>A trivial trackable so a history has something to record.</summary>
    private sealed partial class Box : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IUndoTrackable
    {
        private string _text = "";

        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public IEnumerable<UndoableProperty> UndoableProperties =>
            [UndoableProperty.For(nameof(Text), () => Text, v => Text = v)];
    }

    /// <summary>Hosts a focusable control whose key events reach a window-level tunnel handler wired to
    /// <see cref="UndoGestures.TryHandle"/> — the same shape both real surfaces use.</summary>
    /// <summary>One entry per key seen: whether <see cref="UndoGestures.TryHandle"/> acted, and whether the
    /// key ended up claimed. The two are deliberately different — see the empty-history test.</summary>
    private sealed record Seen(bool Acted, bool Handled);

    private static (Window Window, UndoHistory History, Box Box, List<Seen> Keys) Host()
    {
        Box box = new();
        UndoHistory history = new();
        history.Track(box);

        Button focusable = new() { Content = "x" };
        Window window = new() { Width = 200, Height = 100, Content = focusable };
        List<Seen> keys = [];
        window.AddHandler(
            InputElement.KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                bool acted = UndoGestures.TryHandle(history, e);
                keys.Add(new Seen(acted, e.Handled));
            },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        window.Show();
        focusable.Focus();
        return (window, history, box, keys);
    }

    [Fact]
    public async Task Ctrl_Z_undoes_and_claims_the_key()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, history, box, keys) = Host();
            try
            {
                box.Text = "typed";

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

                Assert.Equal("", box.Text);
                Assert.Contains(new Seen(Acted: true, Handled: true), keys);
                Assert.False(history.CanUndo);
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Both_spellings_of_redo_work()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, history, box, _) = Host();
            try
            {
                box.Text = "typed";
                history.UndoCommand.Execute(null);

                window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
                Assert.Equal("typed", box.Text);

                history.UndoCommand.Execute(null);
                Assert.Equal("", box.Text);

                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.Equal("typed", box.Text);
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_unrelated_key_is_left_alone()
    {
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, _, box, keys) = Host();
            try
            {
                box.Text = "typed";

                window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);

                Assert.Equal("typed", box.Text);
                // Neither acted on NOR claimed: a key that is not one of the three is left completely
                // alone, so whatever else the app binds to it still works.
                Assert.All(keys, k => Assert.Equal(new Seen(Acted: false, Handled: false), k));
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_gesture_with_nothing_to_undo_still_claims_the_key()
    {
        // Claiming it is the point, even though nothing runs. A surface that owns Ctrl+Z must own it
        // consistently: releasing the key once the stack empties hands it to the focused TextBox, whose own
        // undo stack still holds the programmatic writes our restores made — undoing one re-applies the
        // edit we just reversed and records it here as a fresh step, so Ctrl+Z toggles forever.
        await headless.Session.DispatchAsync(async () =>
        {
            var (window, _, _, keys) = Host();
            try
            {
                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
                window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift);

                Assert.NotEmpty(keys);
                Assert.All(keys, k => Assert.Equal(new Seen(Acted: false, Handled: true), k));
            }
            finally { window.Close(); }
            await Task.CompletedTask;
        }, CancellationToken.None);
    }
}
