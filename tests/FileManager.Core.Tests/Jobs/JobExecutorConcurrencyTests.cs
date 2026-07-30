using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Jobs contending for the same paths. The orchestrator runs jobs on a bounded pool, so two
/// deliveries can reach the executor at once and collide on a source or a final path; the path-lock
/// registry is what makes the outcome deterministic.
/// <para>Every assertion here holds for <em>all</em> interleavings — never "the second one wins" —
/// because the scheduler order is not something the product guarantees. What it does guarantee is that
/// no file is ever half-written, duplicated, or lost.</para></summary>
public sealed class JobExecutorConcurrencyTests
{
    [Fact]
    public async Task Two_jobs_racing_one_final_path_leave_exactly_one_intact_file()
    {
        using JobExecutorHarness h = new("conc-samefinal");
        string a = h.WriteSource("a.txt", "content from A");
        string b = h.WriteSource("b.txt", "content from B");
        string final = h.TargetPath("shared.txt");

        JobCompletion[] results = await Task.WhenAll(
            h.Executor.ExecuteAsync(h.Plan(a, final, h.Policy(conflict: ConflictResolution.Overwrite))),
            h.Executor.ExecuteAsync(h.Plan(b, final, h.Policy(conflict: ConflictResolution.Overwrite))));

        Assert.All(results, r => Assert.Equal(JobOutcome.Succeeded, r.Outcome));
        // Exactly one file, and it is one of the two payloads in full — never a blend or a truncation.
        string placed = Assert.Single(Directory.GetFiles(h.TargetDir));
        string content = File.ReadAllText(placed);
        Assert.Contains(content, new[] { "content from A", "content from B" });
        Assert.Empty(h.LeftoverArtifacts());
    }

    /// <summary>M:1 source priority (spec §3.4): under Overwrite, a file from a LOWER-priority Source
    /// that collides with a final path a higher-priority Source already placed this session keeps the
    /// existing file. The rule was dead code — the executor passed a hard-coded rank of 0 to the
    /// resolver while the placer recorded the real one, so the two never compared unequal.</summary>
    [Fact]
    public async Task A_lower_priority_source_does_not_overwrite_what_source_zero_placed()
    {
        using JobExecutorHarness h = new("m1-priority");
        M1Fixture m1 = M1Fixture.Create(h);

        // Sequential, not racing: priority is about which SOURCE wins a collision, and the outcome
        // must not depend on scheduling.
        JobCompletion first = await h.Executor.ExecuteAsync(m1.PreferredPlan(h));
        JobCompletion second = await h.Executor.ExecuteAsync(m1.LesserPlan(h));

        Assert.Equal(JobOutcome.Succeeded, first.Outcome);
        Assert.Equal(JobOutcome.Succeeded, second.Outcome);
        Assert.Equal(M1Fixture.PreferredContent, File.ReadAllText(m1.Final(h)));
        Assert.Empty(h.LeftoverArtifacts());
    }

    /// <summary>The mirror image: a HIGHER-priority source arriving second does take the path, so the
    /// fix cannot be "index 1 never writes".</summary>
    [Fact]
    public async Task A_higher_priority_source_overwrites_what_a_lower_one_placed()
    {
        using JobExecutorHarness h = new("m1-priority-rev");
        M1Fixture m1 = M1Fixture.Create(h);

        await h.Executor.ExecuteAsync(m1.LesserPlan(h));
        JobCompletion second = await h.Executor.ExecuteAsync(m1.PreferredPlan(h));

        Assert.Equal(JobOutcome.Succeeded, second.Outcome);
        Assert.Equal(M1Fixture.PreferredContent, File.ReadAllText(m1.Final(h)));
        Assert.Empty(h.LeftoverArtifacts());
    }

    /// <summary>An M:1 profile: two Sources, one Target, same file name in both roots so they collide on
    /// one final path. The profile really does list both roots — the executor reads per-source filter
    /// overrides off <c>Profile.Sources[SourceIndex]</c>, so a rank without a matching Source would not
    /// model anything the engine can produce.</summary>
    private sealed record M1Fixture(Profile Profile, string Preferred, string Lesser, string LesserRoot)
    {
        public const string PreferredContent = "from the preferred source";
        public const string LesserContent = "from the lesser source";

        public static M1Fixture Create(JobExecutorHarness h)
        {
            string lesserRoot = Path.Combine(h.Root, "source2");
            Directory.CreateDirectory(lesserRoot);
            string preferred = h.WriteSource("report.pdf", PreferredContent);
            string lesser = Path.Combine(lesserRoot, "report.pdf");
            File.WriteAllText(lesser, LesserContent);

            Profile profile = TestProfiles.Valid(h.SourceDir, h.TargetDir) with
            {
                Sources = [new SourceConfig { Path = h.SourceDir }, new SourceConfig { Path = lesserRoot }],
            };
            return new M1Fixture(profile, preferred, lesser, lesserRoot);
        }

        public string Final(JobExecutorHarness h) => h.TargetPath("report.pdf");

        public JobPlan PreferredPlan(JobExecutorHarness h) =>
            h.Plan(Preferred, Final(h), Policy(h), Profile, sourceRoot: h.SourceDir);

        public JobPlan LesserPlan(JobExecutorHarness h) =>
            h.Plan(Lesser, Final(h), Policy(h), Profile, sourceRoot: LesserRoot);

        // No explicit sourceIndex: the rank is resolved from the profile exactly as production does, so
        // the test exercises the real derivation rather than asserting against a hand-picked number.
        private static PolicySnapshot Policy(JobExecutorHarness h) =>
            h.Policy(conflict: ConflictResolution.Overwrite);
    }

    /// <summary>Two targets of ONE job resolving to the same final path is refused at preflight, before
    /// anything is written. Nothing separates them at placement time — the path locks are per-job — so
    /// the two bounded-parallel placements would race the same temp→final move and roll the job back on
    /// every attempt, meaning the file was never delivered at all.</summary>
    [Fact]
    public async Task One_job_whose_two_targets_resolve_to_the_same_path_fails_preflight_cleanly()
    {
        using JobExecutorHarness h = new("dup-target");
        string source = h.WriteSource("song.flac", "payload");
        string final = h.TargetPath("song.flac");

        JobCompletion result = await h.Executor.ExecuteAsync(
            h.Plan(source, [final, final], h.Policy(conflict: ConflictResolution.RenameSuffix)));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(JobErrorCode.ConflictUnresolvable, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(h.TargetDir));
        Assert.Empty(h.LeftoverArtifacts());
        Assert.True(File.Exists(source));   // preflight is pre-write, so the source is untouched
    }

    [Fact]
    public async Task Two_jobs_racing_one_desired_name_under_RenameSuffix_keep_both_payloads()
    {
        // The suffix probe reserves candidates through the lock registry, so two concurrent jobs must
        // land on different names rather than both taking the same one.
        using JobExecutorHarness h = new("conc-rename");
        string a = h.WriteSource("a.txt", "content from A");
        string b = h.WriteSource("b.txt", "content from B");
        string desired = h.TargetPath("shared.txt");

        JobCompletion[] results = await Task.WhenAll(
            h.Executor.ExecuteAsync(h.Plan(a, desired, h.Policy(conflict: ConflictResolution.RenameSuffix))),
            h.Executor.ExecuteAsync(h.Plan(b, desired, h.Policy(conflict: ConflictResolution.RenameSuffix))));

        Assert.All(results, r => Assert.Equal(JobOutcome.Succeeded, r.Outcome));
        string[] placed = Directory.GetFiles(h.TargetDir);
        Assert.Equal(2, placed.Length);
        // Both payloads survived, each complete.
        string[] contents = [.. placed.Select(File.ReadAllText).Order()];
        Assert.Equal(["content from A", "content from B"], contents);
        // One took the desired name; the other was suffixed.
        Assert.Contains(placed, p => Path.GetFileName(p) == "shared.txt");
        Assert.Empty(h.LeftoverArtifacts());
    }

    [Fact]
    public async Task Two_jobs_for_the_same_source_are_serialized_and_place_one_file()
    {
        // The source path is in every job's lock set, so these cannot interleave. Whichever runs
        // second finds the target already satisfied.
        using JobExecutorHarness h = new("conc-samesource");
        string source = h.WriteSource("doc.txt", "one payload");
        string final = h.TargetPath("doc.txt");

        JobCompletion[] results = await Task.WhenAll(
            h.Executor.ExecuteAsync(h.Plan(source, final)),
            h.Executor.ExecuteAsync(h.Plan(source, final)));

        Assert.Single(Directory.GetFiles(h.TargetDir));
        Assert.Equal("one payload", File.ReadAllText(final));
        // One did the work; the other short-circuited as already-satisfied.
        Assert.Contains(results, r => r.Outcome == JobOutcome.Succeeded);
        Assert.Contains(results, r => r.Outcome == JobOutcome.Skipped
            && r.SkipReason == SkipReason.UnchangedAtAllTargets);
    }

    [Fact]
    public async Task Many_independent_jobs_all_complete_without_interfering()
    {
        using JobExecutorHarness h = new("conc-fanout");
        const int count = 24;
        List<Task<JobCompletion>> running = [];
        for (int i = 0; i < count; i++)
        {
            string source = h.WriteSource($"file-{i:D2}.txt", $"payload {i:D2}");
            running.Add(h.Executor.ExecuteAsync(h.Plan(source, h.TargetPath($"file-{i:D2}.txt"))));
        }

        JobCompletion[] results = await Task.WhenAll(running);

        Assert.All(results, r => Assert.Equal(JobOutcome.Succeeded, r.Outcome));
        Assert.Equal(count, Directory.GetFiles(h.TargetDir).Length);
        for (int i = 0; i < count; i++)
            Assert.Equal($"payload {i:D2}", File.ReadAllText(h.TargetPath($"file-{i:D2}.txt")));
        Assert.Empty(h.LeftoverArtifacts());
    }

    [Fact]
    public async Task A_multi_target_job_writes_all_of_its_targets_correctly_under_parallel_placement()
    {
        // Per-target placement is bounded-parallel, and the targets share one JobExecution and one
        // lock set — so this is the in-job concurrency case, distinct from job-vs-job above.
        using JobExecutorHarness h = new("conc-intra");
        byte[] payload = new byte[512 * 1024];
        new Random(4242).NextBytes(payload);
        string source = h.WriteSourceBytes("big.bin", payload);
        string[] roots = [.. Enumerable.Range(1, 8).Select(i => Path.Combine(h.Root, $"t{i}"))];
        string[] finals = [.. roots.Select(r => Path.Combine(r, "big.bin"))];

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, finals, h.Policy(), targetRoots: roots));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        foreach (string final in finals)
            Assert.Equal(payload, File.ReadAllBytes(final));
        Assert.Empty(h.LeftoverArtifacts());
    }

    [Fact]
    public async Task A_job_whose_source_is_deleted_by_a_racing_disposal_skips_cleanly()
    {
        // Overlap safety (spec §12): two profiles watch one source and one disposes it. The loser must
        // end SKIPPED(SourceDisposed) — not an error, and with no partial target.
        using JobExecutorHarness h = new("conc-disposed");
        string source = h.WriteSource("raced.txt", "one payload");
        string firstTarget = h.TargetPath("first.txt");
        string secondTarget = h.TargetPath("second.txt");

        // The first job disposes the source; the second is planned before that happens.
        JobPlan loser = h.Plan(source, secondTarget, h.Policy(onSuccess: OnSuccessAction.KeepSource));
        JobCompletion winner = await h.Executor.ExecuteAsync(
            h.Plan(source, firstTarget, h.Policy(onSuccess: OnSuccessAction.PermanentDelete)));
        Assert.Equal(JobOutcome.Succeeded, winner.Outcome);
        Assert.False(File.Exists(source));

        JobCompletion result = await h.Executor.ExecuteAsync(loser);

        Assert.Equal(JobOutcome.Skipped, result.Outcome);
        Assert.Equal(SkipReason.SourceDisposed, result.SkipReason);
        Assert.False(File.Exists(secondTarget), "a skipped job must not leave a partial target");
        Assert.Equal("one payload", File.ReadAllText(firstTarget));
    }

    [Fact]
    public async Task Concurrent_jobs_that_all_fail_leave_no_debris_between_them()
    {
        using JobExecutorHarness h = new("conc-allfail");
        h.Hasher.CorruptPathsMatching = path => path.Contains(".fmtmp-", StringComparison.OrdinalIgnoreCase);
        List<Task<JobCompletion>> running = [];
        for (int i = 0; i < 8; i++)
        {
            string source = h.WriteSource($"file-{i}.txt", $"payload {i}");
            running.Add(h.Executor.ExecuteAsync(h.Plan(source, h.TargetPath($"file-{i}.txt"))));
        }

        JobCompletion[] results = await Task.WhenAll(running);

        Assert.All(results, r => Assert.True(r.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed));
        Assert.Empty(Directory.GetFiles(h.TargetDir, "*", SearchOption.AllDirectories));
        Assert.Empty(h.LeftoverArtifacts());
        // Every source is still exactly where it was.
        for (int i = 0; i < 8; i++)
            Assert.Equal($"payload {i}", File.ReadAllText(Path.Combine(h.SourceDir, $"file-{i}.txt")));
    }
}
