using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FileManager.UI.Undo;

/// <summary>Undo/redo for an editing session, plus the dirty flag that falls out of it. Attach the view
/// models whose edits should be reversible (<see cref="Track"/>, <see cref="TrackCollection"/>) and the
/// history records them by listening to the change notifications they already raise — no per-mutation
/// bookkeeping at the call sites, and nothing becomes undoable unless it was declared so.
///
/// Deliberately view-model-shaped, not settings-shaped: it takes any
/// <see cref="INotifyPropertyChanged"/> plus delegates, so the profile editor can use the same instance
/// type without a second mechanism.
///
/// Not thread-safe, and does not need to be — every consumer edits on the UI thread.</summary>
public sealed partial class UndoHistory : ObservableObject
{
    private readonly Stack<IUndoableChange> _undo = new();
    private readonly Stack<IUndoableChange> _redo = new();

    /// <summary>Tracked owners keyed by reference: a view model that overrides equality (a record) must
    /// still be one entry per instance.</summary>
    private readonly Dictionary<object, Dictionary<string, TrackedValue>> _owners = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<object> _collections = new(ReferenceEqualityComparer.Instance);

    /// <summary>The step that was on top when the session was last saved (null = saved with an empty
    /// stack). Compared by reference, which is what makes <see cref="IsDirty"/> correct even when an edit
    /// discards a redo branch the marker was sitting in — a discarded step can never be on top again.</summary>
    private IUndoableChange? _savedMarker;

    /// <summary>False when the step on top must not absorb the next one: right after a save (mutating
    /// the saved marker in place would leave the session reading as clean), after an undo/redo, and
    /// whenever <see cref="BreakMerge"/> says the user moved on.</summary>
    private bool _canMergeTop;

    private int _suppressDepth;
    private int _batchDepth;
    private List<IUndoableChange>? _batch;

    /// <summary>True while an undo or redo is being applied. Recording is off, but the notifications
    /// still flow — coupled change hooks that fight a restore (the profile editor's SyncMode →
    /// ScanDestination rule is the motivating case) must check this the way they check their load flag.</summary>
    public bool IsRestoring { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when the session has moved off the last-saved position. Undoing back to it clears
    /// the flag; a fresh edit made after undoing past it keeps the flag set, because the marker left with
    /// the discarded redo branch.
    ///
    /// Position-based, not value-based: re-typing a value back to what it was still reads as dirty. That
    /// is conventional for a dialog and far simpler than diffing the whole edited state.</summary>
    public bool IsDirty => !ReferenceEquals(Top, _savedMarker);

    private IUndoableChange? Top => _undo.Count > 0 ? _undo.Peek() : null;

    internal int UndoDepth => _undo.Count;
    internal int RedoDepth => _redo.Count;

    private bool Recording => _suppressDepth == 0 && !IsRestoring;

    // ============================ Applying ============================

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0 || IsRestoring)
            return;
        IUndoableChange change = _undo.Pop();
        Apply(change.Undo);
        _redo.Push(change);
        RaiseState();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0 || IsRestoring)
            return;
        IUndoableChange change = _redo.Pop();
        Apply(change.Redo);
        _undo.Push(change);
        RaiseState();
    }

    /// <summary>Runs one half of a step with recording off. The flag covers the whole application, not
    /// each individual assignment, because a restored value can cascade into further notifications.</summary>
    private void Apply(Action half)
    {
        IsRestoring = true;
        _canMergeTop = false;
        try
        {
            half();
        }
        finally
        {
            IsRestoring = false;
        }
    }

    // ============================ Session boundaries ============================

    /// <summary>Drops the history and treats the current state as saved — for a (re)load, which replaces
    /// the edited state wholesale and leaves nothing meaningful to step back through.</summary>
    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _savedMarker = null;
        _canMergeTop = false;
        RaiseState();
    }

    /// <summary>Marks the current position as saved, clearing <see cref="IsDirty"/> while leaving the
    /// history intact so the user can still step back through what they just saved.</summary>
    public void MarkSaved()
    {
        _savedMarker = Top;
        _canMergeTop = false;      // never let a later keystroke mutate the marker step in place
        RaiseState();
    }

    /// <summary>Ends the current coalescing run, so the next edit to the same field starts a new undo
    /// step. Called when the user's attention moves — focus leaves the editor, or the navigation tree
    /// jumps elsewhere. Without it, two typing bursts an hour apart would still collapse into one step.</summary>
    public void BreakMerge() => _canMergeTop = false;

    /// <summary>Stops recording for the duration of the scope: for programmatic state changes that are
    /// not user edits (a load, a discard). Notifications still flow and the cached values still refresh,
    /// so the next genuine edit records the right "before" value.</summary>
    public IDisposable Suppress()
    {
        _suppressDepth++;
        return new Scope(() => _suppressDepth--);
    }

    /// <summary>Groups everything recorded in the scope into one undo step — for an edit that cascades,
    /// where undoing only the half the user touched would leave the state inconsistent. Nested scopes
    /// commit once, at the outermost dispose; an empty scope records nothing.</summary>
    public IDisposable Batch()
    {
        if (_batchDepth++ == 0)
            _batch = [];
        return new Scope(CloseBatch);
    }

    private void CloseBatch()
    {
        if (--_batchDepth > 0)
            return;

        List<IUndoableChange> collected = _batch ?? [];
        _batch = null;
        if (collected.Count == 0)
            return;                                     // nothing happened; do not manufacture a dirty step
        Push(collected.Count == 1 ? collected[0] : new CompositeChange(collected));
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }

    // ============================ Recording ============================

    /// <summary>Records a step whose halves the caller supplies — the escape hatch for state no property
    /// recorder can see (a plain backing field). Use inside a <see cref="Batch"/> alongside the property
    /// change it belongs to.</summary>
    public void Record(Action undo, Action redo)
    {
        if (Recording)
            Push(new ActionChange(undo, redo));
    }

    private void Push(IUndoableChange change)
    {
        if (_batchDepth > 0)
        {
            _batch!.Add(change);
            return;
        }

        if (_canMergeTop && Top is { } top && top.TryMerge(change))
        {
            // The step already on the stack absorbed this one; the redo branch is still stale, though.
            _redo.Clear();
            RaiseState();
            return;
        }

        _undo.Push(change);
        _redo.Clear();
        _canMergeTop = true;
        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // ============================ Property tracking ============================

    /// <summary>Records changes to <paramref name="properties"/> on <paramref name="owner"/>. Idempotent:
    /// attaching an already-tracked owner is a no-op, so re-entry through a collection event is safe.</summary>
    public void Track(INotifyPropertyChanged owner, IEnumerable<UndoableProperty> properties)
    {
        if (_owners.ContainsKey(owner))
            return;

        Dictionary<string, TrackedValue> slots = [];
        foreach (UndoableProperty property in properties)
            slots[property.Name] = new TrackedValue(property, property.Get());
        if (slots.Count == 0)
            return;

        _owners[owner] = slots;
        owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    /// <summary>Convenience overload for the common case — a view model that declares its own properties.
    /// Also runs <see cref="IUndoTrackable.TrackNested"/>, so an owner with collections registers them.</summary>
    public void Track(IUndoTrackable owner)
    {
        if (owner is INotifyPropertyChanged notifier)
            Track(notifier, owner.UndoableProperties);
        owner.TrackNested(this);
    }

    public void Untrack(INotifyPropertyChanged owner)
    {
        if (_owners.Remove(owner))
            owner.PropertyChanged -= OnOwnerPropertyChanged;
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is null || e.PropertyName is null)
            return;                                     // a null name means "everything changed" — no before-value to record
        if (!_owners.TryGetValue(sender, out Dictionary<string, TrackedValue>? slots))
            return;
        if (!slots.TryGetValue(e.PropertyName, out TrackedValue? slot))
            return;

        object? previous = slot.Value;
        object? current = slot.Property.Get();

        // Refresh the cache unconditionally, including while suppressed or restoring. Skipping it there
        // would leave a stale "before" value behind, and the next genuine edit would record a step that
        // undoes to a value the user never saw.
        slot.Value = current;

        if (!Recording || slot.Property.AreEqual(previous, current))
            return;                                     // a re-announcement of the same value is not an edit
        Push(new PropertyChange(sender, slot.Property, previous, current));
    }

    private sealed class TrackedValue(UndoableProperty property, object? value)
    {
        public UndoableProperty Property { get; } = property;
        public object? Value { get; set; } = value;
    }

    // ============================ Collection tracking ============================

    /// <summary>Records adds, removes, moves, replacements and clears of <paramref name="items"/>, and
    /// keeps the per-item property tracking in step as rows come and go. Undo re-inserts the very row
    /// instance that was removed, at the index it came from, so nothing is rebuilt and no editor loses
    /// focus.</summary>
    public void TrackCollection<T>(ObservableCollection<T> items)
        where T : class
    {
        if (!_collections.Add(items))
            return;

        // Shadow copy of the contents, because a Reset (Clear) event carries no old items and would
        // otherwise be unrecordable.
        List<T> shadow = [.. items];
        foreach (T item in items)
            TrackItem(item);

        items.CollectionChanged += (_, e) => OnCollectionChanged(items, shadow, e);
    }

    private void OnCollectionChanged<T>(ObservableCollection<T> items, List<T> shadow, NotifyCollectionChangedEventArgs e)
        where T : class
    {
        // Every branch below does its bookkeeping (shadow + per-item tracking) unconditionally and only
        // the recording is gated, so a suppressed load or an in-flight undo leaves the tracker accurate.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
            {
                int index = e.NewStartingIndex;
                foreach (T item in e.NewItems)
                    TrackItem(item);
                List<T> added = Cast<T>(e.NewItems);
                shadow.InsertRange(index, added);
                RecordCollectionEdit(
                    undo: () => RemoveRange(items, index, added.Count),
                    redo: () => InsertRange(items, index, added));
                break;
            }

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
            {
                int index = e.OldStartingIndex;
                List<T> removed = Cast<T>(e.OldItems);
                foreach (T item in removed)
                    UntrackItem(item);
                shadow.RemoveRange(index, removed.Count);
                RecordCollectionEdit(
                    // Re-inserting re-raises Add, which re-tracks the very same instances.
                    undo: () => InsertRange(items, index, removed),
                    redo: () => RemoveRange(items, index, removed.Count));
                break;
            }

            case NotifyCollectionChangedAction.Replace when e.OldItems is not null && e.NewItems is not null:
            {
                int index = e.NewStartingIndex;
                List<T> before = Cast<T>(e.OldItems);
                List<T> after = Cast<T>(e.NewItems);
                foreach (T item in before)
                    UntrackItem(item);
                foreach (T item in after)
                    TrackItem(item);
                for (int i = 0; i < after.Count; i++)
                    shadow[index + i] = after[i];
                RecordCollectionEdit(
                    undo: () => Overwrite(items, index, before),
                    redo: () => Overwrite(items, index, after));
                break;
            }

            case NotifyCollectionChangedAction.Move:
            {
                int from = e.OldStartingIndex, to = e.NewStartingIndex;
                T moved = shadow[from];
                shadow.RemoveAt(from);
                shadow.Insert(to, moved);
                RecordCollectionEdit(undo: () => items.Move(to, from), redo: () => items.Move(from, to));
                break;
            }

            default:
            {
                // Reset — a Clear, or a wholesale replacement. The event carries nothing, so the shadow
                // copy is the only record of what was there; reverse it as a full restore.
                List<T> before = [.. shadow];
                List<T> after = [.. items];
                foreach (T item in before)
                    UntrackItem(item);
                foreach (T item in after)
                    TrackItem(item);
                shadow.Clear();
                shadow.AddRange(after);
                RecordCollectionEdit(undo: () => Fill(items, before), redo: () => Fill(items, after));
                break;
            }
        }
    }

    private void RecordCollectionEdit(Action undo, Action redo)
    {
        if (Recording)
            Push(new ActionChange(undo, redo));
    }

    private void TrackItem(object item)
    {
        if (item is IUndoTrackable trackable)
            Track(trackable);
    }

    private void UntrackItem(object item)
    {
        if (item is INotifyPropertyChanged notifier)
            Untrack(notifier);
    }

    private static List<T> Cast<T>(System.Collections.IList source)
    {
        List<T> result = new(source.Count);
        foreach (object? item in source)
            result.Add((T)item!);
        return result;
    }

    private static void InsertRange<T>(ObservableCollection<T> items, int index, List<T> values)
    {
        for (int i = 0; i < values.Count; i++)
            items.Insert(index + i, values[i]);
    }

    private static void RemoveRange<T>(ObservableCollection<T> items, int index, int count)
    {
        for (int i = 0; i < count; i++)
            items.RemoveAt(index);
    }

    private static void Overwrite<T>(ObservableCollection<T> items, int index, List<T> values)
    {
        for (int i = 0; i < values.Count; i++)
            items[index + i] = values[i];
    }

    private static void Fill<T>(ObservableCollection<T> items, List<T> values)
    {
        items.Clear();
        foreach (T value in values)
            items.Add(value);
    }
}
