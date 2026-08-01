using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Spec §12: "a forced failure at each lifecycle phase leaves the source intact and Targets
/// clean (and, under <c>StageOverwrites</c>, restores replaced files)."
/// <para>Each test injects a failure at one phase and then asserts the same three things about the
/// filesystem: the source is byte-identical, no half-written target survives, and any pre-existing
/// target file is exactly as it was. Those three together are what "safe to retry" means.</para></summary>
public sealed class JobExecutorFailureRollbackTests
{
    private const string SourceContent = "the original payload";
    private const string PriorContent = "the pre-existing target version";

    /// <summary>The invariant every failure path shares, asserted in one place.</summary>
    private static void AssertSourceIntactAndNoDebris(JobExecutorHarness h, string source)
    {
        Assert.True(File.Exists(source), "I-SOURCE-RB: a failed job must never move or delete the source");
        Assert.Equal(SourceContent, File.ReadAllText(source));
        IReadOnlyList<string> leftovers = h.LeftoverArtifacts();
        Assert.True(leftovers.Count == 0,
            $"a failed job must leave no temp artifacts; found:{Environment.NewLine}{string.Join(Environment.NewLine, leftovers)}");
    }

    // ---- failure at each phase -------------------------------------------------------------------

    [Fact]
    public async Task A_preflight_shortfall_fails_before_writing_anything()
    {
        using JobExecutorHarness h = new("fail-preflight");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Volumes.Free = 1;

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.InsufficientDiskSpace, completion.Error?.Code);
        Assert.False(File.Exists(final));
        AssertSourceIntactAndNoDebris(h, source);
        // I-WAL: nothing was written, so the job closes directly without a rollback sweep.
        Assert.Contains(h.JournalRecords().OfType<JobClosedRecord>(), r => r.Outcome == JobOutcome.Failed);
        Assert.DoesNotContain(h.JournalRecords(), r => r is RollbackBeginRecord);
    }

    [Fact]
    public async Task A_target_resolving_onto_the_source_fails_as_a_self_path()
    {
        using JobExecutorHarness h = new("fail-selfpath");
        string source = h.WriteSource("doc.txt", SourceContent);

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, source));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.SelfPathTarget, completion.Error?.Code);
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task A_seal_read_failure_fails_the_job_without_touching_the_target()
    {
        using JobExecutorHarness h = new("fail-seal");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        // Fail hashing the SOURCE (the seal), before any target work begins. Budget exceeds the retry
        // allowance so it is a persistent failure.
        h.Hasher.FailTransientlyForPathsMatching = path => path.Equals(source, StringComparison.OrdinalIgnoreCase);
        h.Hasher.TransientFailureCount = 99;

        JobCompletion completion = await h.ExecuteWithRetriesAsync(h.Plan(source, final));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.False(File.Exists(final));
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task A_verification_mismatch_rolls_back_and_leaves_no_target()
    {
        using JobExecutorHarness h = new("fail-verify");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Hasher.CorruptPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);

        JobCompletion completion = await h.ExecuteWithRetriesAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.VerificationMismatch, completion.Error?.Code);
        Assert.False(File.Exists(final), "a target that failed read-back verification must not be placed");
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task A_metadata_failure_under_FailJob_rolls_back_and_leaves_no_target()
    {
        using JobExecutorHarness h = new("fail-metadata");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Metadata.FailApply = true;

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(metadataOnConflict: MetadataOnConflict.FailJob)));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.MetadataConflict, completion.Error?.Code);
        Assert.False(File.Exists(final));
        AssertSourceIntactAndNoDebris(h, source);
    }

    /// <summary>The counterpart: metadata is best-effort, so the same fault must NOT cost the placement.
    /// <para><see cref="Fakes.FakeMetadataPreserver.PolicyBlind"/> is essential here. By default the fake
    /// mirrors <c>WindowsMetadataPreserver</c> and resolves the policy itself, returning Success under
    /// WarnAndContinue — so the placer never saw a failure and this test asserted nothing about it. Blind
    /// mode reports the failure regardless, which is what pins the PLACER's own handling.</para></summary>
    [Fact]
    public async Task A_metadata_failure_under_WarnAndContinue_still_places_the_file()
    {
        using JobExecutorHarness h = new("warn-metadata");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Metadata.FailApply = true;
        h.Metadata.PolicyBlind = true;

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(metadataOnConflict: MetadataOnConflict.WarnAndContinue)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal(SourceContent, File.ReadAllText(final));
        Assert.Contains(h.Metadata.Applied, a => a.OnConflict == MetadataOnConflict.WarnAndContinue);
        Assert.Empty(h.LeftoverArtifacts());
    }

    /// <summary>And FailJob still fails against the same blind preserver, so the two policies are
    /// genuinely distinguished by the placer rather than by the fake.</summary>
    [Fact]
    public async Task A_metadata_failure_under_FailJob_fails_even_when_the_preserver_is_policy_blind()
    {
        using JobExecutorHarness h = new("blind-failjob");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Metadata.FailApply = true;
        h.Metadata.PolicyBlind = true;

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(metadataOnConflict: MetadataOnConflict.FailJob)));

        Assert.Equal(JobOutcome.Failed, completion.Outcome);
        Assert.Equal(JobErrorCode.MetadataConflict, completion.Error?.Code);
        AssertSourceIntactAndNoDebris(h, source);
    }

    // ---- the StageOverwrites restore criterion ---------------------------------------------------

    [Fact]
    public async Task StageOverwrites_restores_the_replaced_file_when_the_job_fails_after_placement()
    {
        // Spec §12's "restores replaced files". Failing the job-committed append is the only
        // deterministic way to fail AFTER a target is placed — verification and metadata both fail
        // before the replace — so this is the path that actually exercises the staged restore.
        using JobExecutorHarness h = new("stage-restore");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), PriorContent);
        h.Journal.FailOnRecordType = typeof(JobCommittedRecord);

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(overwrite: OverwriteHandling.StageOverwrites)));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.Equal(1, h.Journal.FailedAppends);
        // The user's pre-existing file is back, byte-for-byte.
        Assert.True(File.Exists(final), "the replaced file must be restored, not left missing");
        Assert.Equal(PriorContent, File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task DirectOverwrite_cannot_restore_the_replaced_file_which_is_why_it_is_not_the_default()
    {
        // Pins the documented trade-off rather than pretending it does not exist: DirectOverwrite
        // replaces in place with no staged copy, so a post-placement failure leaves the NEW content at
        // the final path and the prior version is unrecoverable. The source is still intact, so no
        // data the engine was given is lost — but the pre-existing target version is gone.
        using JobExecutorHarness h = new("direct-norestore");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), PriorContent);
        h.Journal.FailOnRecordType = typeof(JobCommittedRecord);

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(overwrite: OverwriteHandling.DirectOverwrite)));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.NotEqual(PriorContent, File.ReadAllText(final));
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task A_pre_existing_target_survives_a_failure_that_happens_before_the_replace()
    {
        using JobExecutorHarness h = new("prior-survives");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), PriorContent);
        h.Hasher.CorruptPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);

        JobCompletion completion = await h.ExecuteWithRetriesAsync(
            h.Plan(source, final, h.Policy(overwrite: OverwriteHandling.StageOverwrites)));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.Equal(PriorContent, File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
        AssertSourceIntactAndNoDebris(h, source);
    }

    // ---- multi-target failure --------------------------------------------------------------------

    [Fact]
    public async Task A_multi_target_failure_leaves_no_target_placed_and_every_prior_intact()
    {
        // One sibling fails; the whole job must roll back so the user never sees a partial fan-out.
        using JobExecutorHarness h = new("fail-multi");
        string source = h.WriteSource("doc.txt", SourceContent);
        string[] roots = [Path.Combine(h.Root, "t1"), Path.Combine(h.Root, "t2"), Path.Combine(h.Root, "t3")];
        string[] finals = [.. roots.Select(r => Path.Combine(r, "doc.txt"))];
        // Every target has a prior version, so a botched rollback would be visible as changed content.
        foreach (string final in finals)
            h.WriteExistingTarget(final, PriorContent);
        // Only the last target's read-back is corrupted.
        h.Hasher.CorruptPathsMatching = path =>
            path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase)
            && path.Contains("t3", StringComparison.OrdinalIgnoreCase);

        JobCompletion completion = await h.ExecuteWithRetriesAsync(
            h.Plan(source, finals, h.Policy(overwrite: OverwriteHandling.StageOverwrites), targetRoots: roots));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        foreach (string final in finals)
        {
            Assert.True(File.Exists(final), $"{final} must still exist after rollback");
            Assert.Equal(PriorContent, File.ReadAllText(final));
        }
        foreach (string root in roots)
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task A_multi_target_failure_removes_a_freshly_placed_sibling()
    {
        // No prior versions this time: rollback must DELETE what it placed, not leave orphans.
        using JobExecutorHarness h = new("fail-multi-fresh");
        string source = h.WriteSource("doc.txt", SourceContent);
        string[] roots = [Path.Combine(h.Root, "t1"), Path.Combine(h.Root, "t2")];
        string[] finals = [.. roots.Select(r => Path.Combine(r, "doc.txt"))];
        h.Hasher.CorruptPathsMatching = path =>
            path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase)
            && path.Contains("t2", StringComparison.OrdinalIgnoreCase);

        JobCompletion completion = await h.ExecuteWithRetriesAsync(
            h.Plan(source, finals, h.Policy(), targetRoots: roots));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        foreach (string final in finals)
            Assert.False(File.Exists(final), $"{final} must not survive a rolled-back job");
        AssertSourceIntactAndNoDebris(h, source);
    }

    // ---- retry budget (spec §12) -----------------------------------------------------------------

    [Fact]
    public async Task A_transiently_failing_read_back_succeeds_within_the_retry_budget_without_rolling_back()
    {
        using JobExecutorHarness h = new("retry-transient");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        // Two failures, then success — inside the 3-attempt budget.
        h.Hasher.FailTransientlyForPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);
        h.Hasher.TransientFailureCount = 2;

        JobCompletion completion = await h.ExecuteWithRetriesAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal(2, h.Hasher.TransientFailuresServed);   // the retries really happened
        Assert.Equal(SourceContent, File.ReadAllText(final));
        Assert.DoesNotContain(h.JournalRecords(), r => r is RollbackBeginRecord);
    }

    [Fact]
    public async Task A_persistently_failing_read_back_rolls_back_after_the_budget_is_spent()
    {
        using JobExecutorHarness h = new("retry-persistent");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Hasher.FailTransientlyForPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);
        h.Hasher.TransientFailureCount = 99;   // never recovers

        JobCompletion completion = await h.ExecuteWithRetriesAsync(h.Plan(source, final));

        Assert.True(completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);
        Assert.Equal(3, h.Hasher.TransientFailuresServed);   // exactly the 3-attempt budget, no more
        Assert.False(File.Exists(final));
        AssertSourceIntactAndNoDebris(h, source);
    }

    [Fact]
    public async Task A_verification_mismatch_is_never_retried_because_corruption_is_deterministic()
    {
        // Retrying a mismatch would hide corruption behind "retrying". Exactly one hash of the temp.
        using JobExecutorHarness h = new("no-retry-mismatch");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        int tempHashes = 0;
        h.Hasher.CorruptPathsMatching = path =>
        {
            bool isTemp = path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);
            if (isTemp)
                Interlocked.Increment(ref tempHashes);
            return isTemp;
        };

        JobCompletion completion = await h.ExecuteWithRetriesAsync(h.Plan(source, final));

        Assert.Equal(JobErrorCode.VerificationMismatch, completion.Error?.Code);
        Assert.Equal(1, tempHashes);
        Assert.False(File.Exists(final));
    }

    // ---- a failed job leaves the system retryable -------------------------------------------------

    [Fact]
    public async Task A_job_that_failed_can_be_re_run_successfully_once_the_fault_clears()
    {
        // The point of leaving things clean: the next attempt behaves exactly like a first attempt.
        using JobExecutorHarness h = new("retryable");
        string source = h.WriteSource("doc.txt", SourceContent);
        string final = h.TargetPath("doc.txt");
        h.Hasher.CorruptPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);

        JobCompletion failed = await h.ExecuteWithRetriesAsync(h.Plan(source, final));
        Assert.True(failed.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed);

        h.Hasher.CorruptPathsMatching = null;   // the fault clears
        JobCompletion second = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, second.Outcome);
        Assert.Equal(SourceContent, File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
        Assert.Empty(h.LeftoverArtifacts());
    }
}
