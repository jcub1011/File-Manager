using FileManager.Contracts.Profiles;
using System.Collections.Generic;

namespace FileManager.Core.Runs;

/// <summary>Chooses which Source a file is taken from when more than one of a profile's Sources offers
/// the same destination path (the M:1 collision of spec §3.4).
///
/// <para><b>This is a seam, not a feature.</b> <see cref="PrioritySourceSelector"/> reproduces today's
/// behaviour exactly — the earliest Source in the profile wins — and is the only implementation. It
/// exists so that a future performance strategy (preferring the faster volume when two Sources hold
/// byte-identical copies, and balancing total bytes read across similar volumes) is a change to
/// PLANNING only. The chosen source is recorded on the snapshot's copy item, so execution never has to
/// re-decide and can never disagree with what was displayed.</para>
///
/// <para><b>The intended strategy is throughput-driven, not heuristic — and it will move this seam to
/// DISPATCH time.</b> Ranking volumes by media type (NVMe &gt; SATA SSD &gt; HDD) was considered and
/// abandoned: media type is a poor proxy for throughput, and no static probe can see queue depth,
/// competing processes, NAS congestion, or a drive that is degrading. Instead, dispatch on <em>least
/// outstanding read work per volume</em> (keyed by <c>IVolumeInfoProvider.GetVolumeKey</c>): a faster
/// volume drains its in-flight work sooner, so it looks more available, so it receives more. That
/// self-calibrates with nothing to measure and no hardware probing at all.
/// <para>Because that decision needs live per-volume load, it cannot stay a plan-time choice — so
/// expect this interface to be re-shaped rather than merely implemented. The load-bearing property
/// survives either way: <c>RunCopyItem.SourceIndex</c> records what actually happened, so the snapshot
/// and the audit trail agree with reality even when the choice was not made in advance.</para></para>
///
/// <para><b>What gates the valuable half.</b> Electing a source only *saves* work if the losing
/// candidates are never read — which means collapsing a duplicate pair into ONE job. Today such a pair
/// is two jobs and each disposes its own source, so collapsing would leave the loser's file undisposed
/// under <c>MoveToTrash</c>/<c>PermanentDelete</c>. "Dispose all replicas" needs
/// <c>JobOpenedRecord.Source</c> to become a set and one audit row per disposed source. Two findings
/// make the rest safe: the destination is source-independent (M:1 forces <c>Flatten</c>), and
/// <c>ConflictResolver.PriorityKeepsExisting</c> is order-independent, so election cannot corrupt
/// content under Overwrite/OverwriteIfNewer. Full analysis in
/// <c>docs/mirror-run-next-steps.md</c> §8–§9.</para></summary>
public interface ISourceSelector
{
    /// <summary>Picks the winning candidate for one contested destination path.</summary>
    /// <param name="profile">The profile being planned, for its Source ordering.</param>
    /// <param name="candidates">Two or more files that all resolve to the same destination path. Never
    /// empty; a single-candidate path never reaches a selector.</param>
    /// <returns>The candidate to copy from. Must be one of <paramref name="candidates"/>.</returns>
    SourceCandidate Select(Profile profile, IReadOnlyList<SourceCandidate> candidates);
}

/// <summary>One file a Source offers for a contested destination path.</summary>
/// <param name="SourcePath">The file itself.</param>
/// <param name="SourceRoot">The configured Source root it was found under.</param>
/// <param name="SourceIndex">Its position in the profile's Sources — the §3.4 priority rank. -1 when
/// its root matched none.</param>
/// <param name="SizeBytes">For a future byte-balancing strategy; unused by the priority selector.</param>
public readonly record struct SourceCandidate(
    string SourcePath, string SourceRoot, int SourceIndex, long SizeBytes);

/// <summary>Today's behaviour, unchanged: the earliest Source in the profile wins a contested
/// destination path (spec §3.4). An unmatched source (index -1) ranks highest, which is the
/// conservative choice — it never lets an unrecognized root be silently passed over.</summary>
public sealed class PrioritySourceSelector : ISourceSelector
{
    public SourceCandidate Select(Profile profile, IReadOnlyList<SourceCandidate> candidates)
    {
        SourceCandidate best = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            if (Rank(candidates[i]) < Rank(best))
                best = candidates[i];
        }
        return best;
    }

    /// <summary>The comparable rank. Mirrors <c>JobPlan.PriorityIndex</c>: an unmatched source (-1)
    /// becomes 0 rather than sorting last, so the two sides of the M:1 rule agree.</summary>
    private static int Rank(SourceCandidate candidate) =>
        candidate.SourceIndex < 0 ? 0 : candidate.SourceIndex;
}
