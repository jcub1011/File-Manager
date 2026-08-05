using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Jobs;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

/// <summary>Replaying a pending run's frozen plan as the frames a preview streams.
///
/// <para>Every case here runs the <b>real</b> planner into a <b>real</b> snapshot on disk and asserts what
/// comes back out, because the defect this file exists for was invisible to any test that scripted the
/// frames: the handler faithfully emitted everything the snapshot held, and the snapshot held no
/// destination side at all. So the GUI showed source rows with no targets, and — for any profile that
/// removes nothing — a completely empty destination panel. A test that hands the handler a ready-made
/// report can never catch that; only one that goes through the writer can.</para></summary>
public sealed class GetRunPlanStreamHandlerTests
{
    /// <summary>The whole replay, flattened the way a client's sink accumulates it: files appended in
    /// receive order so operation indices — which are global — resolve against them.</summary>
    private sealed record Replay(
        List<DryRunFileColumns> _unused,
        List<(string Path, long Length)> SourceFiles,
        List<(string Path, long Length)> DestinationFiles,
        List<(OperationKind Kind, int SourceIndex, int SubjectIndex, string Path)> SourceOps,
        List<(OperationKind Kind, int SourceIndex, int SubjectIndex, string Path)> DestinationOps,
        DryRunCompleteResponse? Completion,
        ErrorResponse? Error);

    private static async Task<Replay> ReplayAsync(string runDirectory, Guid runId)
    {
        GetRunPlanStreamHandler handler = new(
            new SingleSnapshotCoordinator(runId, runDirectory),
            NullLogger<GetRunPlanStreamHandler>.Instance);

        List<DryRunDirectory> directories = [];
        List<(string, long)> sourceFiles = [];
        List<(string, long)> destinationFiles = [];
        List<(OperationKind, int, int, string)> sourceOps = [];
        List<(OperationKind, int, int, string)> destinationOps = [];
        DryRunCompleteResponse? completion = null;
        ErrorResponse? error = null;

        await foreach (IpcResponse response in handler.HandleStreamAsync(
            new GetRunPlanStreamRequest { RunId = runId }))
        {
            switch (response)
            {
                case DryRunChunkResponse chunk:
                    // Directories accumulate across chunks and every index is a position into the
                    // concatenated table, exactly as IpcClient's reassembly treats them.
                    for (int d = 0; d < chunk.DirectoryName.Count; d++)
                        directories.Add(new DryRunDirectory(chunk.DirectoryName[d], chunk.DirectoryParentIndex[d]));
                    Collect(chunk.SourceFiles, sourceFiles);
                    Collect(chunk.DestinationFiles, destinationFiles);
                    CollectOps(chunk.SourceOperations, sourceOps);
                    CollectOps(chunk.DestinationOperations, destinationOps);
                    break;
                case DryRunCompleteResponse done:
                    completion = done;
                    break;
                case ErrorResponse failed:
                    error = failed;
                    break;
            }
        }

        return new Replay([], sourceFiles, destinationFiles, sourceOps, destinationOps, completion, error);

        void Collect(DryRunFileColumns columns, List<(string, long)> into)
        {
            for (int i = 0; i < columns.FileName.Count; i++)
                into.Add((Join(columns.DirIndex[i], columns.FileName[i]), columns.Length[i]));
        }

        void CollectOps(DryRunOperationColumns columns, List<(OperationKind, int, int, string)> into)
        {
            for (int i = 0; i < columns.Kind.Count; i++)
                into.Add((columns.Kind[i], columns.SourceIndex[i], columns.SubjectIndex[i],
                    Join(columns.DirIndex[i], columns.FileName[i])));
        }

        string Join(int dirIndex, string fileName)
        {
            List<string> parts = [fileName];
            for (int at = dirIndex; at >= 0; at = directories[at].ParentIndex)
                parts.Insert(0, directories[at].Name);
            return string.Join(Path.DirectorySeparatorChar, parts);
        }
    }

    // ---- the regression this file exists for ------------------------------------------------------

    [Fact]
    public async Task An_ADDITIVE_plan_still_has_a_destination_side()
    {
        // The reported symptom, pinned at its source: a profile that removes nothing has no orphans, so
        // if the only destination rows a replay can produce are orphans then its destination panel is
        // empty for every non-Mirror profile — and the user cannot tell that from "found nothing".
        using RunPlanHarness h = new("replay-additive");
        h.WriteSource("a.txt", "aaa");
        h.WriteSource("nested/b.txt", "bb");
        Guid runId = Guid.NewGuid();

        (string dir, _, Result completion) = await h.PlanAsync(h.AdditiveProfile(), runId: runId);
        Assert.False(completion.TryGetError(out string? planError), planError);

        Replay replay = await ReplayAsync(dir, runId);

        Assert.Null(replay.Error);
        Assert.Equal(2, replay.SourceFiles.Count);
        Assert.NotEmpty(replay.DestinationOps);
        // Every planned copy lands somewhere, and says where.
        Assert.All(replay.DestinationOps, op => Assert.Equal(OperationKind.New, op.Kind));
        Assert.Contains(replay.DestinationOps, op => op.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Contains(replay.DestinationOps, op => op.Path.EndsWith("b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_source_row_names_the_destination_its_content_lands_at()
    {
        // The other half of the same defect: source rows arrived with an empty target fan-out, because a
        // copy item records only where the file is READ from.
        using RunPlanHarness h = new("replay-fanout");
        h.WriteSource("only.txt", "content");
        Guid runId = Guid.NewGuid();

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(), runId: runId);
        Replay replay = await ReplayAsync(dir, runId);

        var source = Assert.Single(replay.SourceFiles);
        Assert.EndsWith("only.txt", source.Path, StringComparison.Ordinal);
        var destination = Assert.Single(replay.DestinationOps);
        // SourceIndex is what the client groups by to build a source row's target list, so a wrong or
        // absent value is an unattributed destination — a row floating with no source.
        Assert.Equal(0, destination.SourceIndex);
        Assert.EndsWith("only.txt", destination.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_OVERWRITE_names_the_existing_file_it_replaces()
    {
        // The blast radius the approval view is for. Without the subject the client cannot tell an
        // overwrite from a new file, so the Overwrite count — the number that says this run destroys
        // something — reads zero.
        using RunPlanHarness h = new("replay-overwrite");
        h.WriteSource("clash.txt", "new content");
        h.WriteTarget("clash.txt", "old");
        Guid runId = Guid.NewGuid();

        // The harness's default conflict policy is Skip, which is its own (also subject-bearing) kind —
        // Overwrite is the one this case is about, so ask for it.
        Profile profile = h.AdditiveProfile(scanDestination: true);
        (string dir, _, _) = await h.PlanAsync(
            profile with
            {
                Policies = profile.Policies with { ConflictResolution = ConflictResolution.Overwrite },
            },
            runId: runId);
        Replay replay = await ReplayAsync(dir, runId);

        var overwrite = Assert.Single(replay.DestinationOps, op => op.Kind == OperationKind.Overwrite);
        Assert.True(overwrite.SubjectIndex >= 0, "an overwrite must name the file it replaces");
        // The subject resolves to the file that is actually there, with the size it actually has — which
        // is what the view shows as "was 3 bytes, becomes 11".
        var subject = replay.DestinationFiles[overwrite.SubjectIndex];
        Assert.EndsWith("clash.txt", subject.Path, StringComparison.Ordinal);
        Assert.Equal(3, subject.Length);
    }

    [Fact]
    public async Task A_SKIPPED_conflict_still_names_the_file_that_caused_the_skip()
    {
        // Under Skip the run writes nothing here, and the row's whole meaning is the existing file it
        // deferred to. A subject-less skip would read as "went somewhere, unclear where".
        using RunPlanHarness h = new("replay-skip");
        h.WriteSource("clash.txt", "new content");
        h.WriteTarget("clash.txt", "old");
        Guid runId = Guid.NewGuid();

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(scanDestination: true), runId: runId);
        Replay replay = await ReplayAsync(dir, runId);

        var skip = Assert.Single(replay.DestinationOps, op => op.Kind == OperationKind.SkipConflict);
        Assert.True(skip.SubjectIndex >= 0);
        Assert.Equal(3, replay.DestinationFiles[skip.SubjectIndex].Length);
    }

    [Fact]
    public async Task An_ALREADY_SYNCHRONIZED_MIRROR_plan_still_has_a_source_side()
    {
        // The second half of the reported symptom. Every source is already at the target, so the plan has
        // nothing to copy — and a replay built from the copy list therefore has no source rows, leaving the
        // panel blank exactly when the honest answer is "these files, every one already up to date".
        using RunPlanHarness h = new("replay-mirror-synced");
        h.WriteSource("same.txt", "identical");
        h.WriteSource("also-same.txt", "identical too");
        h.WriteTarget("same.txt", "identical");
        h.WriteTarget("also-same.txt", "identical too");
        Guid runId = Guid.NewGuid();

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), runId: runId);
        Assert.Empty(RunPlanHarness.Copies(dir));   // nothing to do, by design

        Replay replay = await ReplayAsync(dir, runId);

        Assert.Equal(2, replay.SourceFiles.Count);
        Assert.All(replay.SourceOps, op => Assert.Equal(OperationKind.SkippedUnchanged, op.Kind));
        // And nothing is up for deletion: an unchanged file still projects a destination, which is what
        // keeps its target off the orphan list.
        Assert.DoesNotContain(replay.DestinationOps, op => op.Kind == OperationKind.Deleted);
    }

    [Fact]
    public async Task A_FILTERED_source_is_replayed_as_the_skip_it_is()
    {
        // A file the filters excluded produces no copy item either, and it is the one row a user most needs
        // to see: "why is this not being backed up?" is answered by its skip detail.
        using RunPlanHarness h = new("replay-filtered");
        h.WriteSource("keep.txt", "keep");
        h.WriteSource("drop.tmp", "drop");
        Guid runId = Guid.NewGuid();

        (string dir, _, _) = await h.PlanAsync(
            h.AdditiveProfile() with { Filters = new FilterSet { ExcludeGlob = ["*.tmp"] } },
            runId: runId);
        Replay replay = await ReplayAsync(dir, runId);

        Assert.Equal(2, replay.SourceFiles.Count);
        Assert.Single(replay.SourceOps, op => op.Kind == OperationKind.SkippedByFilter);
        Assert.Single(replay.SourceOps, op => op.Kind == OperationKind.Processed);
    }

    [Fact]
    public async Task A_MIRROR_plan_replays_its_orphans_alongside_the_projection()
    {
        using RunPlanHarness h = new("replay-mirror");
        h.WriteSource("keep.txt", "keep");
        h.WriteTarget("orphan.txt", "orphaned");
        Guid runId = Guid.NewGuid();

        (string dir, _, _) = await h.PlanAsync(h.MirrorProfile(), runId: runId);
        Replay replay = await ReplayAsync(dir, runId);

        var orphan = Assert.Single(replay.DestinationOps, op => op.Kind == OperationKind.Deleted);
        Assert.EndsWith("orphan.txt", orphan.Path, StringComparison.Ordinal);
        // The orphan's own file record, with the length the deletion pass re-verifies against.
        Assert.Equal(8, replay.DestinationFiles[orphan.SubjectIndex].Length);
        // And the copy is still projected — the two halves share one index space, so emitting both must
        // not corrupt either. This is the assertion that would fail if the passes collided.
        Assert.Contains(replay.DestinationOps, op => op.Kind != OperationKind.Deleted && op.SourceIndex == 0);
    }

    [Fact]
    public async Task The_terminator_carries_the_plans_own_truncation_and_timestamp()
    {
        using RunPlanHarness h = new("replay-terminator");
        h.WriteSource("a.txt", "a");
        Guid runId = Guid.NewGuid();

        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(), runId: runId);
        Replay replay = await ReplayAsync(dir, runId);

        Assert.NotNull(replay.Completion);
        Assert.Equal(DateTimeOffset.UnixEpoch, replay.Completion.GeneratedAt);
        Assert.False(replay.Completion.Truncated);
    }

    [Fact]
    public async Task An_UNKNOWN_run_is_refused_rather_than_answered_with_an_empty_plan()
    {
        // A closed, declined or superseded run is a normal race. It must not read as "this run will do
        // nothing", which is what an empty frame sequence would say.
        using RunPlanHarness h = new("replay-missing");
        h.WriteSource("a.txt", "a");
        Guid runId = Guid.NewGuid();
        (string dir, _, _) = await h.PlanAsync(h.AdditiveProfile(), runId: runId);

        GetRunPlanStreamHandler handler = new(
            new SingleSnapshotCoordinator(runId, dir), NullLogger<GetRunPlanStreamHandler>.Instance);
        List<IpcResponse> responses = [];
        await foreach (IpcResponse response in handler.HandleStreamAsync(
            new GetRunPlanStreamRequest { RunId = Guid.NewGuid() }))
        {
            responses.Add(response);
        }

        Assert.Equal("RUN_NOT_FOUND", Assert.IsType<ErrorResponse>(Assert.Single(responses)).Code);
    }

    /// <summary>Answers with one run's snapshot directory and nothing else — the only thing the handler
    /// asks a coordinator for.</summary>
    private sealed class SingleSnapshotCoordinator(Guid runId, string directory) : IRunCoordinator
    {
        public string? SnapshotDirectory(Guid id) => id == runId ? directory : null;

        public Result<RunHandle, string> Begin(Profile profile, string? scopePath) => "not used";
        public Result Approve(Guid id, bool approve, bool acknowledgeWarnings = false) => Result.Success();
        public Result Cancel(Guid id) => Result.Success();
        public Result Discard(Guid id) => Result.Success();
        public RunStatus? GetStatus(Guid id) => null;
        public void Settled(Guid id, JobCompletion? completion) { }
        public Profile? PlannedProfile(Guid id) => null;
        public void Coalesced(Guid id) { }
        public IReadOnlyList<RunSummaryDto> ListRuns() => [];
        public Result SetPaused(Guid id, bool paused) => Result.Success();
        public Task StopAsync() => Task.CompletedTask;
    }
}
