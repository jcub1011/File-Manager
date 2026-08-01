using System;
using System.Collections.Generic;

namespace FileManager.UI.Undo;

/// <summary>One property that participates in undo/redo, described by delegates rather than by a name
/// the history has to reflect over. That is deliberate: the UI publishes NativeAOT, so a reflection-based
/// property setter would be a trimming hazard. The cost is one line per undoable property, which also
/// makes the opt-in explicit — nothing becomes undoable by accident.</summary>
/// <param name="Name">The property name as it arrives on <see cref="System.ComponentModel.PropertyChangedEventArgs"/>.</param>
/// <param name="Get">Reads the current value, boxed.</param>
/// <param name="Set">Writes a value back during undo/redo, unboxed by the closure.</param>
/// <param name="AreEqual">Value comparison, captured from the property's own type so a change that
/// re-announces the same value records nothing.</param>
/// <param name="Coalesce">True for values a user produces a keystroke or a click at a time (text, numbers):
/// consecutive changes collapse into one undo step. False for discrete picks (enums, booleans), where each
/// change is its own step.</param>
public sealed record UndoableProperty(
    string Name,
    Func<object?> Get,
    Action<object?> Set,
    Func<object?, object?, bool> AreEqual,
    bool Coalesce)
{
    /// <summary>Declares a property from typed accessors — the form every call site should use, e.g.
    /// <c>UndoableProperty.For(nameof(Value), () =&gt; Value, v =&gt; Value = v, coalesce: true)</c>.
    /// The equality comparer comes from <typeparamref name="T"/> so records compare structurally and
    /// value types never box into reference comparison.</summary>
    public static UndoableProperty For<T>(string name, Func<T> get, Action<T> set, bool coalesce = false) =>
        new(name,
            () => get(),
            value => set((T)value!),
            (a, b) => EqualityComparer<T>.Default.Equals((T)a!, (T)b!),
            coalesce);
}

/// <summary>A view model whose edits can be undone. Implementers list the properties they want recorded;
/// the history subscribes to <see cref="System.ComponentModel.INotifyPropertyChanged"/> and does the rest.
///
/// Deliberately an interface rather than a base-class virtual: the consumers are unrelated types
/// (settings items, settings rows, and next the profile editor and its rows), so a shared base would
/// have to be invented — or a second mechanism bolted on beside it.</summary>
public interface IUndoTrackable
{
    /// <summary>The recorded properties. Called once per attach, so an expression-bodied
    /// <c>=&gt; [...]</c> implementation is fine.</summary>
    IEnumerable<UndoableProperty> UndoableProperties { get; }

    /// <summary>Hook for state that is not a scalar property — collections, mostly. Called right after
    /// <see cref="UndoableProperties"/> is registered. Default: nothing to do.</summary>
    void TrackNested(UndoHistory history) { }
}
