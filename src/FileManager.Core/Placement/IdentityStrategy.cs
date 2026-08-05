using FileManager.Contracts.Profiles;
using System;

namespace FileManager.Core.Placement;

/// <summary>What kind of content read proves identity. Only reached when metadata alone did not
/// settle the question.</summary>
public enum IdentityEvidence
{
    /// <summary>Stream the whole file under the job's <see cref="VerificationMethod"/>. Exact.</summary>
    FullHash,

    /// <summary>Hash a bounded set of windows (<see cref="SampledHashLayout"/>). Exact when it says the
    /// files DIFFER; probabilistic when it says they are identical.</summary>
    SampledHash,
}

/// <summary>The evidence the spec §3.4.1 unchanged-check will accept for one particular file.
/// Produced by <see cref="IdentityStrategy.For"/>.</summary>
public readonly record struct IdentityPlan
{
    /// <summary>Equal size AND equal last-write time (within
    /// <see cref="IdentityStrategy.TimestampTolerance"/>) is accepted as identical, reading zero bytes
    /// of content.</summary>
    public required bool AcceptMetadataMatch { get; init; }

    /// <summary>Consulted when metadata did not establish identity. <c>null</c> means "declare the files
    /// different" — there is no content read left to try.</summary>
    public required IdentityEvidence? ContentEvidence { get; init; }
}

/// <summary>The incoming file as the §3.4.1 unchanged-check sees it: its metadata, the evidence its
/// size and the job's policy call for, and whichever digest that evidence needs — computed ONCE by the
/// caller and reused across every target, rather than re-read per target.
///
/// <para>Passed in rather than read off <c>JobExecution.Output</c> because the check now runs BEFORE the
/// output is sealed: on a duplicate, sealing's full read is the cost the whole feature exists to
/// avoid.</para></summary>
public sealed record IdentityReference
{
    /// <summary>The incoming file — the sealed output, which for a no-transformer job is the source.</summary>
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteUtc { get; init; }
    public required IdentityPlan Plan { get; init; }

    /// <summary>Hex full-content digest under the job's <see cref="VerificationMethod"/>. Required when
    /// <see cref="IdentityPlan.ContentEvidence"/> is <see cref="IdentityEvidence.FullHash"/>.</summary>
    public string? FullContentHash { get; init; }

    /// <summary>Sampled digest under <see cref="SampledHashLayout.Default"/>. Required when
    /// <see cref="IdentityPlan.ContentEvidence"/> is <see cref="IdentityEvidence.SampledHash"/>.</summary>
    public byte[]? SampledContentHash { get; init; }
}

/// <summary>THE single rule deciding how the §3.4.1 unchanged-file short-circuit proves that a
/// destination already holds the incoming content.
///
/// <para>Deliberately a pure static, in the spirit of
/// <see cref="Profile.ComputeEffectiveScanDestination"/>: both the preview
/// (<c>DryRunEngine.EvaluateTargetAsync</c>) and the real run (<c>JobExecutor</c>'s probe phase →
/// <c>AtomicPlacer.CheckUnchangedAsync</c>) call it, so the plan the user approves and the plan that
/// executes cannot disagree about what counts as a duplicate. Anything added here is added to both at
/// once; anything special-cased in one of them is a bug.</para>
///
/// <para>This governs IDENTITY only. The post-copy read-back verification of every file actually
/// written is unaffected and always a full content hash under the job's
/// <see cref="VerificationMethod"/> — see <c>AtomicPlacer.VerifyAsync</c>.</para></summary>
public static class IdentityStrategy
{
    /// <summary>Last-write comparison tolerance. FAT/exFAT round last-write times to ~2 s, so an exact
    /// tick compare would never short-circuit on those volumes. Shared so the placer and the dry-run
    /// engine cannot drift to different tolerances.</summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>True when two last-write times are equal for identity purposes.</summary>
    public static bool TimestampsMatch(DateTimeOffset a, DateTimeOffset b) =>
        (a - b).Duration() <= TimestampTolerance;

    /// <summary>Decides the evidence for one file.
    ///
    /// <para>Ordering matters. The <see cref="VerificationMethod"/> gate comes first because
    /// <c>None</c>/<c>SizeTimestamp</c> mean "no content hashing anywhere in this job", which no identity
    /// setting may override. The size threshold comes next because a small file is exact for free — there
    /// is nothing to buy by guessing about it.</para></summary>
    /// <param name="sizeBytes">Size of the incoming file (the sealed output).</param>
    /// <param name="verification">The job's verification method — the algorithm for
    /// <see cref="IdentityEvidence.FullHash"/>, and the gate on content hashing at all.</param>
    /// <param name="identity">The profile's identity policy for files above the threshold.</param>
    /// <param name="thresholdBytes">Files at or below this size always use
    /// <see cref="IdentityEvidence.FullHash"/>.</param>
    public static IdentityPlan For(
        long sizeBytes,
        VerificationMethod verification,
        LargeFileIdentity identity,
        long thresholdBytes)
    {
        // Verification None/SizeTimestamp: no content hash is available to compare against, so identity
        // falls back to size + mtime exactly as it did before this feature existed (spec §3.4.1).
        if (verification is not (VerificationMethod.Sha256 or VerificationMethod.XxHash128))
            return new IdentityPlan { AcceptMetadataMatch = true, ContentEvidence = null };

        // At or below the threshold the full hash is cheap and exact — never trade that away.
        if (sizeBytes <= thresholdBytes)
            return new IdentityPlan { AcceptMetadataMatch = false, ContentEvidence = IdentityEvidence.FullHash };

        return identity switch
        {
            LargeFileIdentity.FullHash =>
                new IdentityPlan { AcceptMetadataMatch = false, ContentEvidence = IdentityEvidence.FullHash },
            LargeFileIdentity.SampledHash =>
                new IdentityPlan { AcceptMetadataMatch = false, ContentEvidence = IdentityEvidence.SampledHash },
            LargeFileIdentity.TimestampOrSampledHash =>
                new IdentityPlan { AcceptMetadataMatch = true, ContentEvidence = IdentityEvidence.SampledHash },
            LargeFileIdentity.SizeAndTimestamp =>
                new IdentityPlan { AcceptMetadataMatch = true, ContentEvidence = null },
            // An unknown member can only come from a profile JSON written by a newer build. Fall back to
            // the exact behaviour rather than guessing at a cheaper one.
            _ => new IdentityPlan { AcceptMetadataMatch = false, ContentEvidence = IdentityEvidence.FullHash },
        };
    }
}
