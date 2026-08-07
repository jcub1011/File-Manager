using System;
using System.Collections;
using System.Collections.Generic;

namespace FileManager.UI.ViewModels;

/// <summary>The bound view over a set of dry-run rows: an array of store keys plus a factory that
/// turns one into a row handle on demand. Materializes a handle per indexer access, so a 500k-row
/// preview costs 4 bytes a row here and allocates only for the rows the viewport actually realizes.
///
/// <para><b>The non-generic <see cref="IList"/> implementation is load-bearing, not incidental.</b>
/// Avalonia's <c>ItemsSourceView</c> uses <see cref="IList"/> for indexed access and falls back to
/// copying the whole source into a list when the bound collection does not implement it — which
/// would materialize every handle up front and undo the entire point of this type. It used to be
/// satisfied by accident, because the bound instance was a <c>List&lt;T&gt;</c>.
/// <c>DryRunRowStoreTests.The_bound_row_list_is_an_IList</c> pins it.</para>
///
/// <para>Handles are values, not identities: two handles for the same row are <c>Equals</c> because a
/// row record is a record over (store, key). That is what lets selection, <see cref="IndexOf"/> and
/// container recycling keep working across re-materialization.</para></summary>
internal sealed class DryRunRowList<T> : IReadOnlyList<T>, IList<T>, IList
    where T : class
{
    private readonly int[] _keys;
    private readonly Func<int, T> _rowAt;
    private readonly Func<T, int> _keyOf;

    /// <param name="keys">Store keys, in display order. Taken by reference and never mutated.</param>
    /// <param name="rowAt">Builds the row handle at a <em>list position</em> — not a store key, so a
    /// Destinations list can also reach the per-position entry slice it carries alongside the keys.</param>
    /// <param name="keyOf">A row's store key — used by <see cref="IndexOf"/> to find the one position
    /// that could match by comparing integers, instead of materializing every row.</param>
    public DryRunRowList(int[] keys, Func<int, T> rowAt, Func<T, int> keyOf)
    {
        _keys = keys;
        _rowAt = rowAt;
        _keyOf = keyOf;
    }

    /// <summary>The underlying store keys in display order — what a rebuild filters and what
    /// <c>DryRunRebuild.SameKeys</c> compares.</summary>
    public int[] Keys => _keys;

    public int Count => _keys.Length;

    public T this[int index] => _rowAt(index);

    /// <summary>The position of a row, or -1. Scanning the keys narrows it to the one position that
    /// could match (a key appears at most once), then the candidate is materialized and compared in
    /// full — the key alone would also match a row of the same index from a <em>different</em> store,
    /// or one carrying a different entry slice, neither of which is this row.</summary>
    public int IndexOf(T item)
    {
        if (item is null)
            return -1;
        int key = _keyOf(item);
        for (int i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] == key)
                return EqualityComparer<T>.Default.Equals(_rowAt(i), item) ? i : -1;
        }
        return -1;
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < _keys.Length; i++)
            array[arrayIndex + i] = this[i];
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _keys.Length; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── Read-only surface ────────────────────────────────────────────────────────────────────────
    // A rebuild republishes a whole new list rather than mutating one, so every mutator throws.

    private static NotSupportedException ReadOnly() =>
        new("a dry-run row list is read-only; a rebuild publishes a new list instead");

    T IList<T>.this[int index] { get => this[index]; set => throw ReadOnly(); }
    bool ICollection<T>.IsReadOnly => true;
    void IList<T>.Insert(int index, T item) => throw ReadOnly();
    void IList<T>.RemoveAt(int index) => throw ReadOnly();
    void ICollection<T>.Add(T item) => throw ReadOnly();
    void ICollection<T>.Clear() => throw ReadOnly();
    bool ICollection<T>.Remove(T item) => throw ReadOnly();

    object? IList.this[int index] { get => this[index]; set => throw ReadOnly(); }
    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => _keys;
    int IList.Add(object? value) => throw ReadOnly();
    void IList.Clear() => throw ReadOnly();
    bool IList.Contains(object? value) => value is T row && Contains(row);
    int IList.IndexOf(object? value) => value is T row ? IndexOf(row) : -1;
    void IList.Insert(int index, object? value) => throw ReadOnly();
    void IList.Remove(object? value) => throw ReadOnly();
    void IList.RemoveAt(int index) => throw ReadOnly();

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < _keys.Length; i++)
            array.SetValue(this[i], index + i);
    }
}
