using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace FileManager.UI.ViewModels;

/// <summary>The bound view over a paged plan half: as many positions as the view has rows, each
/// materialized from whichever page is resident — or as a placeholder while that page is on its way.
///
/// <para>The paged counterpart to <see cref="DryRunRowList{T}"/>, and it inherits that type's
/// load-bearing detail: <b>the non-generic <see cref="IList"/> implementation is not incidental</b>.
/// Avalonia's <c>ItemsSourceView</c> uses it for indexed access and falls back to COPYING the whole
/// source into a list when it is missing — which here would try to materialize every row of a plan that
/// deliberately is not resident, i.e. exactly the failure this whole design exists to prevent.
/// <c>DryRunRowStoreTests.The_bound_row_list_is_an_IList</c> pins the same property for the non-paged
/// list.</para>
///
/// <para><b>Why placeholders rather than waiting.</b> The indexer is synchronous and runs on the UI
/// thread during layout, so a row whose page has not arrived cannot be awaited without freezing the
/// window. It returns a placeholder immediately, and the store's page-arrived event turns into a
/// <see cref="NotifyCollectionChangedAction.Replace"/> over just that range — the standard
/// virtualized-feed contract. Prefetching either side of the viewport is what keeps a placeholder off
/// the screen during ordinary scrolling; they show up when the user drags the thumb across a large
/// plan, which is the case where the alternative is a frozen window.</para>
///
/// <para>Read-only, like its sibling: a rebuild publishes a new list rather than mutating one, so every
/// mutator throws.</para></summary>
internal sealed class PagedDryRunRowList<T> : IReadOnlyList<T>, IList<T>, IList, INotifyCollectionChanged
    where T : class
{
    private readonly PagedDryRunRowStore _store;
    private readonly Func<DryRunRowStore, int, int, T> _rowAt;
    private readonly Func<int, T> _placeholderAt;

    /// <param name="rowAt">Builds a row from its resident page store, its index within that page, and
    /// its position in the view — the last so a row can report where it sits without the page knowing.</param>
    /// <param name="placeholderAt">Builds the stand-in shown while a page is in flight.</param>
    public PagedDryRunRowList(
        PagedDryRunRowStore store,
        Func<DryRunRowStore, int, int, T> rowAt,
        Func<int, T> placeholderAt)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _rowAt = rowAt;
        _placeholderAt = placeholderAt;
        _store.PageArrived += OnPageArrived;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _store.RowCount;

    public T this[int index] =>
        _store.TryGetRow(index, out DryRunRowStore page, out int within)
            ? _rowAt(page, within, index)
            : _placeholderAt(index);

    /// <summary>Re-reads the rows a landed page covers. A Replace over the range rather than a Reset:
    /// Reset makes the list re-read everything and drops scroll position and selection, which on a
    /// paged list would also re-trigger fetches for pages the user has already scrolled past.
    ///
    /// <para>Avalonia's Replace handling wants the changed items, and building them here is bounded by
    /// the page size — and they are exactly the rows now resident, so nothing is materialized that was
    /// not just paid for.</para></summary>
    private void OnPageArrived(object? sender, (int First, int Count) range)
    {
        if (CollectionChanged is not { } handler)
            return;
        int first = Math.Max(0, range.First);
        int last = Math.Min(Count, range.First + range.Count);
        if (last <= first)
            return;

        List<T> replaced = new(last - first);
        for (int i = first; i < last; i++)
            replaced.Add(this[i]);
        handler(this, new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Replace, replaced, replaced, first));
    }

    /// <summary>Detaches from the store. A list left subscribed would keep raising changes for a preview
    /// that is no longer on screen, and keep itself alive through the store's event.</summary>
    public void Detach() => _store.PageArrived -= OnPageArrived;

    /// <summary>Linear by necessity — a paged list has no key array to scan, and a row's identity is its
    /// position. Callers use it for selection, which is a single row at a time.</summary>
    public int IndexOf(T item)
    {
        if (item is null)
            return -1;
        for (int i = 0; i < Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(this[i], item))
                return i;
        }
        return -1;
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < Count; i++)
            array[arrayIndex + i] = this[i];
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── Read-only surface ────────────────────────────────────────────────────────────────────────

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
    object ICollection.SyncRoot => _store;
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
        for (int i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }
}
