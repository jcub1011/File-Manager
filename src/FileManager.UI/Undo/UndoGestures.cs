using Avalonia.Input;
using System.Windows.Input;

namespace FileManager.UI.Undo;

/// <summary>The undo/redo keyboard contract, in one place. Every editing surface that offers undo binds
/// the same three gestures to the same commands, so declaring them per view invited the two copies from
/// drifting apart — and the strings here are the ones the buttons quote in their tooltips.
///
/// Deliberately not a <c>KeyBinding</c> in markup: the callers register these as tunnelling (preview)
/// handlers so the surface sees Ctrl+Z before a focused <c>TextBox</c> consumes it, which a KeyBinding
/// cannot express.</summary>
public static class UndoGestures
{
    public static KeyGesture Undo { get; } = KeyGesture.Parse("Ctrl+Z");
    public static KeyGesture Redo { get; } = KeyGesture.Parse("Ctrl+Y");

    /// <summary>The second spelling of redo, for hands that learned it from editors rather than from
    /// Office.</summary>
    public static KeyGesture RedoAlt { get; } = KeyGesture.Parse("Ctrl+Shift+Z");

    /// <summary>Runs undo or redo on <paramref name="history"/> when <paramref name="e"/> is one of the
    /// gestures, and reports whether it acted.
    ///
    /// A recognised gesture is ALWAYS marked handled, including when the stack is empty and nothing runs.
    /// That is not tidiness — it is the fix for a toggle bug. A surface that owns Ctrl+Z has to own it
    /// consistently: letting the key fall through once the history is exhausted hands it to the focused
    /// <c>TextBox</c>, whose own undo stack still holds the programmatic writes this history's restores
    /// made. Undoing one of those re-applies the edit that was just reversed, and — because it arrives
    /// through the two-way binding like any user edit — records a fresh step here. Ctrl+Z then flips
    /// between the two values forever instead of walking backwards.
    ///
    /// Callers decide whether their surface is in scope before calling; a key that is not one of the three
    /// gestures is left completely alone.</summary>
    public static bool TryHandle(UndoHistory history, KeyEventArgs e)
    {
        ICommand? command =
            Undo.Matches(e) ? history.UndoCommand
            : Redo.Matches(e) || RedoAlt.Matches(e) ? history.RedoCommand
            : null;

        if (command is null)
            return false;

        e.Handled = true;
        if (!command.CanExecute(null))
            return false;
        command.Execute(null);
        return true;
    }
}
