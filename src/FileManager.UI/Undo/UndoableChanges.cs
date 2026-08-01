using System;
using System.Collections.Generic;

namespace FileManager.UI.Undo;

/// <summary>One reversible step on <see cref="UndoHistory"/>'s stacks. Kept internal: callers describe
/// what is undoable (via <see cref="UndoableProperty"/>, <see cref="UndoHistory.TrackCollection"/>, or
/// <see cref="UndoHistory.Record"/>) and never construct steps themselves.</summary>
internal interface IUndoableChange
{
    void Undo();
    void Redo();

    /// <summary>Folds <paramref name="next"/> into this step, so a burst of keystrokes is one undo.
    /// Returns false when the two are not the same continuing edit. The caller decides whether merging
    /// is permitted at all (see <see cref="UndoHistory.BreakMerge"/>); this only decides whether the two
    /// steps are compatible.</summary>
    bool TryMerge(IUndoableChange next);
}

/// <summary>A single property assignment, reversed by writing the previous value back through the
/// property's own setter.</summary>
internal sealed class PropertyChange : IUndoableChange
{
    private readonly object _owner;
    private readonly UndoableProperty _property;
    private readonly object? _oldValue;
    private object? _newValue;

    public PropertyChange(object owner, UndoableProperty property, object? oldValue, object? newValue)
    {
        _owner = owner;
        _property = property;
        _oldValue = oldValue;
        _newValue = newValue;
    }

    public void Undo() => _property.Set(_oldValue);
    public void Redo() => _property.Set(_newValue);

    public bool TryMerge(IUndoableChange next)
    {
        // Only values a user builds up incrementally coalesce. A pick from a dropdown or a checkbox
        // toggle is a decision, and each decision deserves its own undo step.
        if (!_property.Coalesce || next is not PropertyChange other)
            return false;
        if (!ReferenceEquals(other._owner, _owner)
            || !string.Equals(other._property.Name, _property.Name, StringComparison.Ordinal))
            return false;

        // Keep this step's original oldValue — undoing the merged run lands where the burst started.
        _newValue = other._newValue;
        return true;
    }
}

/// <summary>A step described by its two halves. Backs the collection edits (whose inverse is another
/// collection edit, not a value assignment) and <see cref="UndoHistory.Record"/>, the escape hatch for
/// state a property recorder cannot see — a plain backing field, say.</summary>
internal sealed class ActionChange(Action undo, Action redo) : IUndoableChange
{
    public void Undo() => undo();
    public void Redo() => redo();

    /// <summary>Never merges: these steps are discrete structural edits, and folding two of them would
    /// silently drop one half of the pair.</summary>
    public bool TryMerge(IUndoableChange next) => false;
}

/// <summary>Several steps that undo as one, produced by <see cref="UndoHistory.Batch"/>. Undo runs the
/// children in reverse and redo runs them forward, which is what makes a coupled edit (a property whose
/// change hook drives a second property) restore correctly rather than half-way.</summary>
internal sealed class CompositeChange(List<IUndoableChange> children) : IUndoableChange
{
    public void Undo()
    {
        for (int i = children.Count - 1; i >= 0; i--)
            children[i].Undo();
    }

    public void Redo()
    {
        for (int i = 0; i < children.Count; i++)
            children[i].Redo();
    }

    public bool TryMerge(IUndoableChange next) => false;
}
