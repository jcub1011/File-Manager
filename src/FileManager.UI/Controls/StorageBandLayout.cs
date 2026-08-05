using System;

namespace FileManager.UI.Controls;

/// <summary>Which figure a band on the storage bar represents. Kept separate from the brushes and
/// label text so the layout can be decided — and tested — without a rendering context.</summary>
public enum StorageBandKind
{
    /// <summary>Space in use on the volume right now, untouched by the run.</summary>
    CurrentUsed,
    /// <summary>Growth: how much more the volume holds once the run settles.</summary>
    UsedAtRest,
    /// <summary>Shrinkage: space the run releases, drawn where the run ends below where it started.</summary>
    Freed,
    /// <summary>The concurrency-aware high-water mark during the run.</summary>
    RealisticPeak,
    /// <summary>The worst-case high-water mark during the run.</summary>
    AbsolutePeak,
}

/// <summary>One band's place on the bar: the threshold it reaches, and the span it actually shows once
/// the bands are layered.</summary>
/// <param name="Kind">Which figure this band represents.</param>
/// <param name="Threshold">The band's upper bound in bytes — every band starts at zero.</param>
/// <param name="VisibleFrom">Lower edge of the span left uncovered by smaller bands, in bytes.</param>
public readonly record struct StorageBand(StorageBandKind Kind, double Threshold, double VisibleFrom)
{
    /// <summary>False when a larger band covers this one completely, which happens whenever two of the
    /// figures coincide (a run with no transient overhead, say). Such a band is painted but has no span
    /// of its own, so it gets neither a label nor a hover target.</summary>
    public bool IsVisible => Threshold > VisibleFrom;
}

/// <summary>Decides the storage bar's band layout. Every figure the bar draws is a threshold measured
/// from zero, so every band is a bar from zero to its threshold; ordering them largest-first lets each
/// smaller bar cover the one beneath it, and each band ends up showing exactly the span the next-smaller
/// one does not claim.
/// <para>
/// Sorting rather than assuming an order is what makes this correct for any input. The usual reading is
/// <c>current → at-rest → realistic peak → worst case</c>, but that order is not guaranteed: a run that
/// frees space settles below where the volume started, and how far its peak climbs back over that mark
/// depends on the profile — files awaiting deletion, staged overwrites, concurrency. Any band can
/// therefore end up on either side of any other.
/// </para></summary>
public static class StorageBandLayout
{
    /// <summary>Builds the bands largest-first. <paramref name="realisticPeak"/> and
    /// <paramref name="safeCeiling"/> are raised to the figures they are defined to bound, so a caller
    /// passing an inconsistent set still gets a coherent bar.</summary>
    public static StorageBand[] Compute(double usedNow, double settled, double realisticPeak, double safeCeiling)
    {
        double peak = Math.Max(settled, realisticPeak);
        double ceiling = Math.Max(peak, safeCeiling);

        // The one pairing that is not a fixed threshold is the at-rest total against the current total:
        // whichever is larger is the band describing the change (growth, or released space) and
        // whichever is smaller is the untouched baseline.
        bool grows = settled >= usedNow;

        (StorageBandKind Kind, double Threshold)[] layers =
        [
            (StorageBandKind.AbsolutePeak, ceiling),
            (StorageBandKind.RealisticPeak, peak),
            grows ? (StorageBandKind.UsedAtRest, settled) : (StorageBandKind.Freed, usedNow),
            // The smaller threshold is the untouched baseline, and WHICH figure that is flips with the
            // direction: growing, the volume's current total is the baseline and settled is above it;
            // freeing, the at-rest total is the baseline and the current total is above it. Tagging this
            // band CurrentUsed unconditionally mislabelled the at-rest total "CSU / Current Storage Used"
            // on any volume the run frees — reporting 900 GB as current when the drive holds 1000 GB right
            // now — and left the bar with no SUAR reading at all.
            (grows ? StorageBandKind.CurrentUsed : StorageBandKind.UsedAtRest, grows ? usedNow : settled),
        ];
        Array.Sort(layers, static (a, b) => b.Threshold.CompareTo(a.Threshold));

        var bands = new StorageBand[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            double visibleFrom = i + 1 < layers.Length ? layers[i + 1].Threshold : 0;
            bands[i] = new StorageBand(layers[i].Kind, layers[i].Threshold, visibleFrom);
        }
        return bands;
    }
}
