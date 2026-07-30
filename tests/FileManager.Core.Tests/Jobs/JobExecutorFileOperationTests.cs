using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>What ends up on disk after a successful job, asserted against the real filesystem.
/// <para>Covers spec §12's topology criterion (1:1, 1:N, M:1, M:N), byte-level content fidelity, the
/// four conflict resolutions, all three verification methods, and idempotency. Every assertion is
/// about observable file state — content, count, names, timestamps — because that is what a user
/// loses if this is wrong.</para></summary>
public sealed class JobExecutorFileOperationTests
{
    // ---- topologies (spec §12: "each topology produces the documented Target state") --------------

    [Fact]
    public async Task One_source_to_one_target_places_exactly_one_file_with_identical_bytes()
    {
        using JobExecutorHarness h = new("exec-1to1");
        string source = h.WriteSource("report.txt", "the payload");
        string final = h.TargetPath("report.txt");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("the payload", File.ReadAllText(final));
        Assert.Equal("the payload", File.ReadAllText(source));
        Assert.Single(Directory.GetFiles(h.TargetDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task One_source_to_many_targets_places_identical_content_at_every_target()
    {
        using JobExecutorHarness h = new("exec-1toN");
        string source = h.WriteSource("fan.bin", "fan-out payload");
        string[] roots = [Path.Combine(h.Root, "t1"), Path.Combine(h.Root, "t2"), Path.Combine(h.Root, "t3")];
        string[] finals = [.. roots.Select(r => Path.Combine(r, "fan.bin"))];

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, finals, h.Policy(), targetRoots: roots));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        foreach (string final in finals)
        {
            Assert.True(File.Exists(final), $"target {final} was not placed");
            Assert.Equal("fan-out payload", File.ReadAllText(final));
        }
        // Exactly one file per target root — no duplicates, no strays.
        foreach (string root in roots)
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Many_sources_to_one_target_place_side_by_side_without_interfering()
    {
        using JobExecutorHarness h = new("exec-Mto1");
        string a = h.WriteSource("a.txt", "content A");
        string b = h.WriteSource("b.txt", "content B");
        string c = h.WriteSource("c.txt", "content C");

        foreach ((string source, string name) in new[] { (a, "a.txt"), (b, "b.txt"), (c, "c.txt") })
        {
            JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, h.TargetPath(name)));
            Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        }

        Assert.Equal(3, Directory.GetFiles(h.TargetDir).Length);
        Assert.Equal("content A", File.ReadAllText(h.TargetPath("a.txt")));
        Assert.Equal("content B", File.ReadAllText(h.TargetPath("b.txt")));
        Assert.Equal("content C", File.ReadAllText(h.TargetPath("c.txt")));
    }

    [Fact]
    public async Task Many_sources_to_many_targets_produce_the_full_cross_product()
    {
        using JobExecutorHarness h = new("exec-MtoN");
        string[] roots = [Path.Combine(h.Root, "t1"), Path.Combine(h.Root, "t2")];
        (string Source, string Name)[] sources =
        [
            (h.WriteSource("one.txt", "1"), "one.txt"),
            (h.WriteSource("two.txt", "22"), "two.txt"),
        ];

        foreach ((string source, string name) in sources)
        {
            string[] finals = [.. roots.Select(r => Path.Combine(r, name))];
            JobCompletion completion = await h.Executor.ExecuteAsync(
                h.Plan(source, finals, h.Policy(), targetRoots: roots));
            Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        }

        // 2 sources × 2 targets = 4 files, each with its own source's content.
        foreach (string root in roots)
        {
            Assert.Equal(2, Directory.GetFiles(root).Length);
            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "one.txt")));
            Assert.Equal("22", File.ReadAllText(Path.Combine(root, "two.txt")));
        }
    }

    // ---- content fidelity ------------------------------------------------------------------------

    [Fact]
    public async Task Binary_content_round_trips_byte_for_byte()
    {
        using JobExecutorHarness h = new("exec-binary");
        // Deterministic bytes spanning the full 0x00–0xFF range, including embedded NULs and the
        // sequences a text-mode copy would mangle (CR, LF, CRLF, EOF marker 0x1A).
        byte[] payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 256);
        payload[10] = 0x00; payload[11] = 0x0D; payload[12] = 0x0A; payload[13] = 0x1A; payload[14] = 0xFF;
        string source = h.WriteSourceBytes("payload.bin", payload);
        string final = h.TargetPath("payload.bin");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal(payload, File.ReadAllBytes(final));
        Assert.Equal(payload, File.ReadAllBytes(source));   // the source is never rewritten
    }

    [Fact]
    public async Task A_file_larger_than_the_copy_buffer_round_trips_byte_for_byte()
    {
        using JobExecutorHarness h = new("exec-large");
        // Comfortably past any internal copy/hash buffer, so a partial-read bug cannot hide.
        byte[] payload = new byte[3 * 1024 * 1024 + 517];   // deliberately not a buffer multiple
        Random rng = new(20260730);
        rng.NextBytes(payload);
        string source = h.WriteSourceBytes("big.bin", payload);
        string final = h.TargetPath("big.bin");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal(payload.Length, new FileInfo(final).Length);
        Assert.Equal(payload, File.ReadAllBytes(final));
    }

    [Fact]
    public async Task An_empty_file_is_placed_as_an_empty_file()
    {
        using JobExecutorHarness h = new("exec-empty");
        string source = h.WriteSourceBytes("empty.dat", []);
        string final = h.TargetPath("empty.dat");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.True(File.Exists(final));
        Assert.Equal(0, new FileInfo(final).Length);
    }

    [Theory]
    [InlineData("café-résumé.txt")]
    [InlineData("日本語のファイル.txt")]
    [InlineData("emoji-🎵-file.txt")]
    [InlineData("spaces and (parens) [brackets].txt")]
    [InlineData("dots.in.the.name.tar.gz")]
    // Shell-significant characters that are still legal Windows filename characters. Double quotes,
    // <, >, |, :, *, ? and / are illegal on Windows, so they cannot be exercised here — the spec's
    // crafted-filename criterion is about transformer argv quoting (Set 4), not placement.
    [InlineData("'quoted' $dollar `backtick` %percent%.txt")]
    [InlineData("$(command-substitution).txt")]
    [InlineData("semi;colon & ampersand.txt")]
    [InlineData("trailing-dash-.txt")]
    [InlineData("-leading-dash.txt")]
    public async Task An_awkward_file_name_is_preserved_exactly(string fileName)
    {
        using JobExecutorHarness h = new("exec-names");
        string source = h.WriteSource(fileName, "awkward name payload");
        string final = h.TargetPath(fileName);

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        string placed = Assert.Single(Directory.GetFiles(h.TargetDir));
        Assert.Equal(fileName, Path.GetFileName(placed));
        Assert.Equal("awkward name payload", File.ReadAllText(placed));
    }

    [Fact]
    public async Task A_nested_relative_path_is_created_under_the_target_root()
    {
        using JobExecutorHarness h = new("exec-nested");
        string source = h.WriteSource(Path.Combine("2026", "07", "deep.txt"), "nested payload");
        string final = h.TargetPath(Path.Combine("2026", "07", "deep.txt"));

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("nested payload", File.ReadAllText(final));
    }

    // ---- verification methods --------------------------------------------------------------------

    [Theory]
    [InlineData(VerificationMethod.Sha256)]
    [InlineData(VerificationMethod.XxHash128)]
    [InlineData(VerificationMethod.None)]
    public async Task Every_verification_method_places_the_file_intact(VerificationMethod method)
    {
        using JobExecutorHarness h = new("exec-verify");
        string source = h.WriteSource("verified.txt", "verify me");
        string final = h.TargetPath("verified.txt");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(verification: method)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("verify me", File.ReadAllText(final));
    }

    // ---- conflict resolution (an existing, DIFFERENT file at the final path) ----------------------

    [Fact]
    public async Task Overwrite_replaces_the_existing_target_and_leaves_one_file()
    {
        using JobExecutorHarness h = new("exec-overwrite");
        string source = h.WriteSource("doc.txt", "new content");
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), "old content");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(conflict: ConflictResolution.Overwrite)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("new content", File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    [Fact]
    public async Task Skip_keeps_the_existing_target_untouched()
    {
        using JobExecutorHarness h = new("exec-skip");
        string source = h.WriteSource("doc.txt", "new content");
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), "old content");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(conflict: ConflictResolution.Skip)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("old content", File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    [Fact]
    public async Task RenameSuffix_places_alongside_the_existing_target_and_keeps_both()
    {
        using JobExecutorHarness h = new("exec-rename");
        string source = h.WriteSource("doc.txt", "new content");
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), "old content");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(conflict: ConflictResolution.RenameSuffix)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        // The prior file must be untouched and the new content must exist under a different name.
        Assert.Equal("old content", File.ReadAllText(final));
        string[] placed = Directory.GetFiles(h.TargetDir);
        Assert.Equal(2, placed.Length);
        string renamed = Assert.Single(placed, p => !p.Equals(final, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("new content", File.ReadAllText(renamed));
        Assert.EndsWith(".txt", renamed, StringComparison.OrdinalIgnoreCase);   // extension preserved
    }

    [Fact]
    public async Task OverwriteIfNewer_replaces_a_target_older_than_the_source()
    {
        using JobExecutorHarness h = new("exec-ifnewer-older");
        string source = h.WriteSource("doc.txt", "new content");
        File.SetLastWriteTimeUtc(source, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc));
        string final = h.WriteExistingTarget(
            h.TargetPath("doc.txt"), "old content", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(conflict: ConflictResolution.OverwriteIfNewer)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("new content", File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    [Fact]
    public async Task OverwriteIfNewer_keeps_a_target_newer_than_the_source()
    {
        using JobExecutorHarness h = new("exec-ifnewer-newer");
        string source = h.WriteSource("doc.txt", "new content");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string final = h.WriteExistingTarget(
            h.TargetPath("doc.txt"), "newer content", new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc));

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(conflict: ConflictResolution.OverwriteIfNewer)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("newer content", File.ReadAllText(final));   // the newer file wins
        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    // ---- idempotency (spec §12) ------------------------------------------------------------------

    [Fact]
    public async Task Re_running_an_unchanged_file_three_times_places_exactly_one_file()
    {
        using JobExecutorHarness h = new("exec-idempotent");
        string source = h.WriteSource("doc.txt", "stable content");
        string final = h.TargetPath("doc.txt");

        JobCompletion first = await h.Executor.ExecuteAsync(h.Plan(source, final));
        Assert.Equal(JobOutcome.Succeeded, first.Outcome);

        for (int i = 0; i < 2; i++)
        {
            JobCompletion repeat = await h.Executor.ExecuteAsync(h.Plan(source, final));
            Assert.Equal(JobOutcome.Skipped, repeat.Outcome);
            Assert.Equal(SkipReason.UnchangedAtAllTargets, repeat.SkipReason);
        }

        Assert.Single(Directory.GetFiles(h.TargetDir, "*", SearchOption.AllDirectories));
        Assert.Equal("stable content", File.ReadAllText(final));
    }

    [Fact]
    public async Task An_unchanged_re_run_does_not_rewrite_the_target_file()
    {
        using JobExecutorHarness h = new("exec-nowrite");
        string source = h.WriteSource("doc.txt", "stable content");
        string final = h.TargetPath("doc.txt");
        await h.Executor.ExecuteAsync(h.Plan(source, final));
        DateTime placedAt = File.GetLastWriteTimeUtc(final);

        await Task.Delay(50);   // any rewrite would move the timestamp forward
        JobCompletion repeat = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Skipped, repeat.Outcome);
        Assert.Equal(placedAt, File.GetLastWriteTimeUtc(final));
    }

    [Fact]
    public async Task RenameSuffix_takes_the_desired_name_when_nothing_is_in_the_way()
    {
        // Regression: the job's own lock set already contains the prospective final path, so a
        // suffix probe that cannot tell "locked by me" from "locked by someone else" would skip the
        // free desired name and place at "doc (1).txt" on a first, collision-free run.
        using JobExecutorHarness h = new("exec-rename-fresh");
        string source = h.WriteSource("doc.txt", "fresh content");
        string final = h.TargetPath("doc.txt");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(conflict: ConflictResolution.RenameSuffix)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        string placed = Assert.Single(Directory.GetFiles(h.TargetDir));
        Assert.Equal("doc.txt", Path.GetFileName(placed));
    }

    [Fact]
    public async Task RenameSuffix_re_delivery_of_an_unchanged_file_produces_no_new_file()
    {
        // Spec §12 idempotency criterion names RenameSuffix specifically: the unchanged-check must run
        // BEFORE conflict resolution, or every re-delivery would accumulate a "(1)", "(2)", … copy.
        using JobExecutorHarness h = new("exec-rename-idempotent");
        string source = h.WriteSource("doc.txt", "stable content");
        string final = h.TargetPath("doc.txt");

        await h.Executor.ExecuteAsync(h.Plan(source, final, h.Policy(conflict: ConflictResolution.RenameSuffix)));
        Assert.Single(Directory.GetFiles(h.TargetDir));

        for (int i = 0; i < 3; i++)
        {
            JobCompletion repeat = await h.Executor.ExecuteAsync(
                h.Plan(source, final, h.Policy(conflict: ConflictResolution.RenameSuffix)));
            Assert.Equal(JobOutcome.Skipped, repeat.Outcome);
            Assert.Equal(SkipReason.UnchangedAtAllTargets, repeat.SkipReason);
        }

        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    [Fact]
    public async Task A_changed_source_replaces_the_previously_placed_file()
    {
        using JobExecutorHarness h = new("exec-changed");
        string source = h.WriteSource("doc.txt", "version one");
        string final = h.TargetPath("doc.txt");
        await h.Executor.ExecuteAsync(h.Plan(source, final));

        File.WriteAllText(source, "version two");
        JobCompletion second = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, second.Outcome);
        Assert.Equal("version two", File.ReadAllText(final));
        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    // ---- filtering -------------------------------------------------------------------------------

    [Fact]
    public async Task An_excluded_file_is_skipped_and_no_target_is_written()
    {
        using JobExecutorHarness h = new("exec-filtered");
        string source = h.WriteSource("scratch.tmp", "should not travel");
        string final = h.TargetPath("scratch.tmp");
        Profile profile = TestProfiles.Valid(h.SourceDir, h.TargetDir) with
        {
            Filters = new FilterSet { ExcludeGlob = ["*.tmp"] },
        };

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(), profile: profile));

        Assert.Equal(JobOutcome.Skipped, completion.Outcome);
        Assert.Equal(SkipReason.Filtered, completion.SkipReason);
        Assert.False(File.Exists(final));
        Assert.Empty(Directory.GetFiles(h.TargetDir, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(source), "a filtered file must be left exactly as it was");
    }

    [Fact]
    public async Task An_included_file_still_travels_when_a_filter_set_is_present()
    {
        using JobExecutorHarness h = new("exec-included");
        string source = h.WriteSource("keep.txt", "should travel");
        string final = h.TargetPath("keep.txt");
        Profile profile = TestProfiles.Valid(h.SourceDir, h.TargetDir) with
        {
            Filters = new FilterSet { Include = ["*.txt"], ExcludeGlob = ["*.tmp"] },
        };

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(), profile: profile));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        Assert.Equal("should travel", File.ReadAllText(final));
    }

    // ---- artifacts & journal ---------------------------------------------------------------------

    [Fact]
    public async Task A_successful_run_leaves_no_temp_or_staging_artifacts_anywhere()
    {
        using JobExecutorHarness h = new("exec-clean");
        string source = h.WriteSource("doc.txt", "new content");
        // Include an overwrite so the staging path is exercised too, not just the fresh-place path.
        string final = h.WriteExistingTarget(h.TargetPath("doc.txt"), "old content");

        JobCompletion completion = await h.Executor.ExecuteAsync(
            h.Plan(source, final, h.Policy(overwrite: OverwriteHandling.StageOverwrites)));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        IReadOnlyList<string> leftovers = h.LeftoverArtifacts();
        Assert.True(leftovers.Count == 0,
            $"a successful run must leave no temp/staging artifacts; found:{Environment.NewLine}{string.Join(Environment.NewLine, leftovers)}");
        Assert.Single(Directory.GetFiles(h.TargetDir));
    }

    [Fact]
    public async Task The_journal_records_the_success_sequence_in_write_ahead_order()
    {
        using JobExecutorHarness h = new("exec-journal");
        string source = h.WriteSource("doc.txt", "journalled");
        string final = h.TargetPath("doc.txt");

        JobCompletion completion = await h.Executor.ExecuteAsync(h.Plan(source, final));

        Assert.Equal(JobOutcome.Succeeded, completion.Outcome);
        List<string> order = [.. h.JournalRecords().Select(r => r.GetType().Name)];

        // I-WAL: the job is opened and the output sealed before any target artifact is recorded, and
        // job-committed — the record that alone authorizes disposition — precedes the terminal close.
        int opened = order.IndexOf(nameof(JobOpenedRecord));
        int sealedAt = order.IndexOf(nameof(OutputSealedRecord));
        int committed = order.IndexOf(nameof(JobCommittedRecord));
        int closed = order.IndexOf(nameof(JobClosedRecord));
        Assert.True(opened >= 0, $"no job-opened record; got [{string.Join(", ", order)}]");
        Assert.True(opened < sealedAt, "output-sealed must follow job-opened");
        Assert.True(sealedAt < committed, "job-committed must follow output-sealed");
        Assert.True(committed < closed, "job-closed must be the terminal record");
        Assert.Equal(closed, order.Count - 1);
        Assert.Contains(h.JournalRecords().OfType<JobClosedRecord>(), r => r.Outcome == JobOutcome.Succeeded);
    }

    [Fact]
    public async Task The_per_job_log_narrates_the_placement()
    {
        using JobExecutorHarness h = new("exec-joblog");
        string source = h.WriteSource("doc.txt", "narrated");
        string final = h.TargetPath("doc.txt");
        JobPlan plan = h.Plan(source, final);

        await h.Executor.ExecuteAsync(plan);

        string log = string.Join("\n", h.JobLogLines(plan.JobId));
        Assert.Contains("opened", log);
        Assert.Contains("placed at", log);
        Assert.Contains("committed", log);
        Assert.Contains("succeeded", log);
    }
}
