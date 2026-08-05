using System;

namespace FileManager.Core.Placement;

/// <summary>Which regions of a file a sampled content hash reads: a head window, N evenly spaced
/// interior windows, and a tail window — a fixed byte budget however large the file is.
///
/// <para>Two sampled digests are comparable ONLY under an identical layout, which is why production
/// always passes <see cref="Default"/>; the parameters exist so tests and benchmarks can vary them.
/// The layout is baked into the digest via <see cref="Version"/>, so changing the window scheme later
/// cannot silently compare incomparable digests — it changes every digest instead.</para></summary>
public readonly record struct SampledHashLayout
{
    /// <summary>Layout version, mixed into the digest prefix. Bump it when the window scheme changes.</summary>
    public const int Version = 1;

    /// <summary>1 MiB windows × 6 interior ⇒ head + 6 + tail = 8 windows = 8 MiB read, at any file size.
    /// 1 MiB matches the read size <c>HashBufferSizeBenchmarks</c> settled on for the streaming hasher, and
    /// eight spread-out windows are enough that a re-encode, a truncation, or a differing header cannot
    /// slip past.</summary>
    public static SampledHashLayout Default { get; } = new()
    {
        WindowSizeBytes = 1024 * 1024,
        InteriorWindowCount = 6,
    };

    public required int WindowSizeBytes { get; init; }

    /// <summary>Windows between the head and the tail. The total window count is this plus two.</summary>
    public required int InteriorWindowCount { get; init; }

    /// <summary>The byte budget: every window read in full.</summary>
    public long BudgetBytes => (long)(InteriorWindowCount + 2) * WindowSizeBytes;

    /// <summary>True when the file is small enough that the windows would cover (or overlap) all of it, so
    /// sampling saves nothing and the digest is taken over the whole file instead.</summary>
    public bool CoversWholeFile(long fileLength) => fileLength <= BudgetBytes;

    /// <summary>How many bytes a sampled hash of this file will actually read — what the run counters must
    /// be told, so a report never claims I/O that never happened.</summary>
    public long BytesRead(long fileLength) =>
        CoversWholeFile(fileLength) ? Math.Max(fileLength, 0) : BudgetBytes;

    /// <summary>Byte offsets of every window, in digest order: head, interior (ascending), tail.
    ///
    /// <para>Interior offsets are spaced <c>(length - W) / (N + 1)</c> apart and rounded DOWN to a window
    /// multiple, which keeps the reads aligned for the I/O stack. The tail is at exactly
    /// <c>length - W</c>, deliberately unaligned — an exact tail is what catches a truncated file. Past
    /// <see cref="CoversWholeFile"/>'s cutoff the spacing is at least <c>W</c>, so windows never
    /// overlap.</para></summary>
    public long[] WindowOffsets(long fileLength)
    {
        if (CoversWholeFile(fileLength))
            return [];

        long w = WindowSizeBytes;
        long span = fileLength - w;
        long[] offsets = new long[InteriorWindowCount + 2];

        offsets[0] = 0;
        for (int i = 1; i <= InteriorWindowCount; i++)
        {
            long offset = i * span / (InteriorWindowCount + 1);
            offsets[i] = offset - (offset % w);      // align down to a window boundary
        }
        offsets[^1] = span;

        return offsets;
    }
}
