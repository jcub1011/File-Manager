using FileManager.Contracts.Profiles;
using FileManager.Core.Placement;

namespace FileManager.Core.Tests.Placement;

/// <summary>The full truth table of the §3.4.1 identity rule. This suite is the guard that the preview
/// and the real run agree about what counts as a duplicate: both call
/// <see cref="IdentityStrategy.For"/>, so pinning it here pins both.</summary>
public sealed class IdentityStrategyTests
{
    private const long Threshold = 256L * 1024 * 1024;
    private const long Small = Threshold - 1;
    private const long Large = Threshold + 1;

    private static IdentityPlan Plan(
        long size, LargeFileIdentity identity, VerificationMethod verification = VerificationMethod.XxHash128) =>
        IdentityStrategy.For(size, verification, identity, Threshold);

    [Theory]
    [InlineData(LargeFileIdentity.FullHash)]
    [InlineData(LargeFileIdentity.SampledHash)]
    [InlineData(LargeFileIdentity.TimestampOrSampledHash)]
    [InlineData(LargeFileIdentity.SizeAndTimestamp)]
    public void At_or_below_the_threshold_every_policy_uses_the_exact_full_hash(LargeFileIdentity identity)
    {
        // A small file is exact for free — no setting may trade that away.
        foreach (long size in new[] { 0L, 1L, Small, Threshold })
        {
            IdentityPlan plan = Plan(size, identity);
            Assert.False(plan.AcceptMetadataMatch);
            Assert.Equal(IdentityEvidence.FullHash, plan.ContentEvidence);
        }
    }

    [Fact]
    public void Above_the_threshold_FullHash_still_reads_both_files_whole()
    {
        IdentityPlan plan = Plan(Large, LargeFileIdentity.FullHash);
        Assert.False(plan.AcceptMetadataMatch);
        Assert.Equal(IdentityEvidence.FullHash, plan.ContentEvidence);
    }

    [Fact]
    public void Above_the_threshold_SampledHash_never_accepts_metadata_alone()
    {
        // Sampling is a cheaper CONTENT check, not a metadata check: matching timestamps must not be
        // enough on their own under this policy.
        IdentityPlan plan = Plan(Large, LargeFileIdentity.SampledHash);
        Assert.False(plan.AcceptMetadataMatch);
        Assert.Equal(IdentityEvidence.SampledHash, plan.ContentEvidence);
    }

    [Fact]
    public void Above_the_threshold_TimestampOrSampledHash_takes_metadata_first_then_falls_back()
    {
        IdentityPlan plan = Plan(Large, LargeFileIdentity.TimestampOrSampledHash);
        Assert.True(plan.AcceptMetadataMatch);
        Assert.Equal(IdentityEvidence.SampledHash, plan.ContentEvidence);
    }

    [Fact]
    public void Above_the_threshold_SizeAndTimestamp_reads_no_content_at_all()
    {
        IdentityPlan plan = Plan(Large, LargeFileIdentity.SizeAndTimestamp);
        Assert.True(plan.AcceptMetadataMatch);
        Assert.Null(plan.ContentEvidence);
    }

    [Theory]
    [InlineData(VerificationMethod.None)]
    [InlineData(VerificationMethod.SizeTimestamp)]
    public void Without_a_hash_based_verification_method_identity_is_metadata_only(VerificationMethod verification)
    {
        // There is no content hash in the job to compare against, so identity degrades to size + mtime
        // exactly as it did before this feature — and no identity setting may override that.
        foreach (LargeFileIdentity identity in Enum.GetValues<LargeFileIdentity>())
        {
            foreach (long size in new[] { Small, Large })
            {
                IdentityPlan plan = IdentityStrategy.For(size, verification, identity, Threshold);
                Assert.True(plan.AcceptMetadataMatch);
                Assert.Null(plan.ContentEvidence);
            }
        }
    }

    [Fact]
    public void Sha256_profiles_get_the_same_tiering_as_XxHash128()
    {
        IdentityPlan plan = Plan(Large, LargeFileIdentity.SampledHash, VerificationMethod.Sha256);
        Assert.Equal(IdentityEvidence.SampledHash, plan.ContentEvidence);

        IdentityPlan small = Plan(Small, LargeFileIdentity.SampledHash, VerificationMethod.Sha256);
        Assert.Equal(IdentityEvidence.FullHash, small.ContentEvidence);
    }

    [Fact]
    public void A_zero_threshold_makes_every_file_large()
    {
        IdentityPlan plan = IdentityStrategy.For(1, VerificationMethod.XxHash128, LargeFileIdentity.SampledHash, 0);
        Assert.Equal(IdentityEvidence.SampledHash, plan.ContentEvidence);
    }

    [Fact]
    public void An_unknown_policy_from_a_newer_build_falls_back_to_the_exact_check()
    {
        // Forward compatibility: a profile written by a newer version must never be read as permission to
        // use a WEAKER check than we understand.
        IdentityPlan plan = IdentityStrategy.For(
            Large, VerificationMethod.XxHash128, (LargeFileIdentity)999, Threshold);
        Assert.False(plan.AcceptMetadataMatch);
        Assert.Equal(IdentityEvidence.FullHash, plan.ContentEvidence);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1999, true)]     // inside the ~2 s FAT/exFAT rounding tolerance
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    [InlineData(-2001, false)]
    public void Timestamp_matching_honours_the_FAT_rounding_tolerance(int driftMs, bool expected)
    {
        DateTimeOffset a = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, IdentityStrategy.TimestampsMatch(a, a.AddMilliseconds(driftMs)));
    }
}
