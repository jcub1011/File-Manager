using System.Collections.Generic;

namespace FileManager.Contracts.DryRun;

/// <summary>The byte-level projection of a dry run: how much data a run would move, how much the
/// destinations would actually grow, and — per destination volume — the settled (at-rest) and
/// bounded-maximum (peak) storage it would consume. Computed over the whole <see cref="DryRunReport"/>
/// graph, so it is null on a truncated report (a partial graph would yield unsound totals).
/// <para>
/// Free space is a physical property of a <em>volume</em> (two target roots on one drive share one
/// pool), so the authoritative used/free math lives on <see cref="VolumeSpaceEstimate"/>. The
/// per-target-root <see cref="VolumeSpaceEstimate.Folders"/> breakdown is byte attribution only, not
/// independent free-space accounting.
/// </para></summary>
public sealed record SpaceProjection
{
    public required IReadOnlyList<VolumeSpaceEstimate> Volumes { get; init; }

    /// <summary>Total bytes read+written across all destinations (the I/O volume): the sum of the
    /// incoming size of every New / Overwrite / Rename write. The executor always stages via
    /// copy-to-temp, so even a same-volume move transfers the full size.</summary>
    public long TotalBytesWritten { get; init; }

    /// <summary>Signed sum of the at-rest change across all volumes (adds minus bytes freed by
    /// overwrites, Mirror deletes, and removed source originals). Can be negative.</summary>
    public long TotalNetChangeBytes { get; init; }

    /// <summary>The per-volume free-space safety margin applied to the fit check (mirrors
    /// <c>EngineConfig.PreflightSafetyMarginBytes</c>). Drawn as the capacity-margin line in the UI.</summary>
    public long SafetyMarginBytes { get; init; }
}

/// <summary>One destination volume's space picture. The four "used" figures are nested thresholds —
/// <c>UsedNow ≤ SettledUsed ≤ RealisticPeakUsed ≤ SafeCeilingUsed</c> — so the UI can paint them as
/// end-to-end increments on a single capacity-width bar.</summary>
public sealed record VolumeSpaceEstimate
{
    /// <summary>The volume's display root (drive root like <c>C:</c> or a UNC share root).</summary>
    public required string VolumeRoot { get; init; }

    /// <summary>False when the volume's capacity/free query failed (e.g. some UNC targets). The
    /// byte/net figures are still valid; the capacity-relative figures (used/free/peak) are not, and
    /// the UI must not draw a fit bar for it.</summary>
    public bool CapacityKnown { get; init; }

    public long TotalCapacityBytes { get; init; }
    public long UsedNowBytes { get; init; }
    public long FreeNowBytes { get; init; }

    /// <summary>The volume's allocation-unit (cluster) size — every file's on-disk footprint is
    /// rounded up to a multiple of this. 1 when unknown (no rounding).</summary>
    public long ClusterBytes { get; init; }

    /// <summary>Data written onto this volume (I/O): Σ incoming size of New/Overwrite/Rename.</summary>
    public long BytesWrittenBytes { get; init; }

    /// <summary>Signed at-rest change on this volume (cluster-rounded adds minus freed bytes).</summary>
    public long NetChangeBytes { get; init; }

    /// <summary>At-rest used space after the run: <c>UsedNow + NetChange</c>.</summary>
    public long SettledUsedBytes { get; init; }

    /// <summary>Concurrency-aware peak used space during the run: settled + staged-overwrite
    /// retention + the transient temp copies of the ~N files in flight at once.</summary>
    public long RealisticPeakUsedBytes { get; init; }

    /// <summary>Fully pessimistic upper bound: settled + staged-overwrite retention + every temp copy
    /// coexisting + workspace need. If this fits, the run is guaranteed to fit.</summary>
    public long SafeCeilingUsedBytes { get; init; }

    /// <summary>Per-target-root byte attribution within this volume (the drill-down). Empty when the
    /// volume has a single target root.</summary>
    public IReadOnlyList<FolderSpaceBreakdown> Folders { get; init; } = [];
}

/// <summary>Byte attribution for one target root, used for the per-volume drill-down.</summary>
public sealed record FolderSpaceBreakdown
{
    public required string Root { get; init; }
    public long BytesWrittenBytes { get; init; }
    public long NetChangeBytes { get; init; }
    public int FileCount { get; init; }
}
