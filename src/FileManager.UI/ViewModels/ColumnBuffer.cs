using System;

namespace FileManager.UI.ViewModels;

/// <summary>An append-only column of <typeparamref name="T"/> stored as fixed-size segments rather
/// than one growing array.
///
/// <para>Two reasons it is not a <c>List&lt;T&gt;</c>. First, growth: a streamed dry run's length is
/// unknown until the terminator arrives, so a doubling column would repeatedly copy itself — with a
/// dozen columns running to the 500k cap that is both a ~1.5× transient spike on top of the very
/// footprint this store exists to cut, and a copy of every element roughly twice over. Appending a
/// segment copies nothing. Second, the LOH: a doubling column crosses the 85,000-byte threshold
/// almost immediately and every later copy churns large-object space, which
/// <c>docs/dry-run-service-memory.md</c> Stage 1 established is the expensive kind of garbage here
/// ("frames on the LOH 5 → 0"). At <see cref="SegmentSize"/> = 8192 the widest segment this store
/// uses is 64 KB (<c>long</c> or a reference), comfortably inside the small-object heap.</para>
///
/// <para>The trade is one extra indirection per access. Both index components fold to a shift and a
/// mask, and the hot passes (filter, search, sort-key build) walk positions in order, so the segment
/// array stays in cache.</para>
///
/// <para>Not thread-safe for writes. Ingest is single-threaded (the stream pump); the parallel passes
/// in the tabs only ever read a completed store.</para></summary>
internal sealed class ColumnBuffer<T>
{
    // 8192 elements: byte 8 KB, int 32 KB, long/reference 64 KB — all below the 85,000-byte LOH line.
    private const int SegmentShift = 13;
    private const int SegmentSize = 1 << SegmentShift;
    private const int SegmentMask = SegmentSize - 1;

    private T[][] _segments = [];
    private int _segmentCount;

    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"the column holds {Count} elements");
            return _segments[index >> SegmentShift][index & SegmentMask];
        }
        set
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"the column holds {Count} elements");
            _segments[index >> SegmentShift][index & SegmentMask] = value;
        }
    }

    public void Add(T value)
    {
        int index = Count;
        int segment = index >> SegmentShift;
        if (segment == _segmentCount)
        {
            if (_segmentCount == _segments.Length)
                Array.Resize(ref _segments, _segments.Length == 0 ? 4 : _segments.Length * 2);
            _segments[_segmentCount++] = new T[SegmentSize];
        }
        _segments[segment][index & SegmentMask] = value;
        Count = index + 1;
    }
}
