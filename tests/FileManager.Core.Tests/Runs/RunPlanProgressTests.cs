using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Runs;
using FileManager.Core.Tests.TestSupport;
using System.Runtime.CompilerServices;

namespace FileManager.Core.Tests.Runs;

/// <summary>Live progress while a run is PLANNING — the phase the GUI calls a dry run.
///
/// <para>The rule under test: <b>a planning run reports what it has found while it is finding it.</b> The
/// counters were always live; what was missing was anyone reading them. The coordinator drove the planner
/// with a plain <c>await foreach</c>, so it sampled only between chunks — and the engine's entire source
/// walk happens inside the FIRST advance, because scan and evaluate are fused and every finding is spooled
/// before a chunk exists. A preview of a large tree therefore published nothing for the whole of its
/// longest phase, and the queue row read "starting the scan" for minutes: a wedged walk and a slow one
/// looked identical, which is the exact complaint the scan counts were added to answer.</para>
///
/// <para>Every test here drives a planner that reproduces that shape deliberately — counters move, no chunk
/// exists — because a planner that yields promptly would pass against the old code too.</para></summary>
public sealed class RunPlanProgressTests
{
    /// <summary>The fix. Asserted while the first advance is still parked, so there is no way for the sample
    /// to have come from a chunk boundary: at this point the planner has produced nothing and cannot until
    /// the test lets it.</summary>
    [Fact]
    public async Task Scan_counts_are_published_while_the_first_chunk_is_still_pending()
    {
        StallingPlanner planner = new(sources: 7);
        using RunPlanHarness h = new("run-plan-progress") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;   // counters have moved; no chunk exists, and none will until Release

        await RunPlanHarness.WaitUntilAsync(
            () => h.Bus.Events.OfType<RunProgressEvent>().Any(e => e.ScannedSources > 0),
            "a planning sample carrying a live scan count", timeoutMs: 5_000);

        RunProgressEvent sample = h.Bus.Events.OfType<RunProgressEvent>().Last(e => e.ScannedSources > 0);
        Assert.Equal(handle!.RunId, sample.RunId);
        Assert.Equal(7, sample.ScannedSources);
        Assert.Equal(0, sample.ScannedDestinations);
        Assert.Equal(nameof(RunPhase.Planning), sample.Phase);
        Assert.Equal(RunPlanStages.Scanning, sample.PlanStage);
        // The whole point: this arrived before anything was planned.
        Assert.Empty(h.Bus.Events.OfType<RunPlannedEvent>());

        planner.Release();
        await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.AwaitingApproval);
    }

    /// <summary>Entries the walk could not read, reported WHILE it happens. The engine also warns about
    /// these once, at approval time — but a share that is failing to answer is worth knowing about before
    /// the user has spent the whole scan believing it was going fine.</summary>
    [Fact]
    public async Task Unreadable_entries_ride_the_live_sample()
    {
        StallingPlanner planner = new(sources: 4, unreadable: 3);
        using RunPlanHarness h = new("run-plan-progress-unreadable") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;
        await RunPlanHarness.WaitUntilAsync(
            () => h.Bus.Events.OfType<RunProgressEvent>().Any(e => e.UnreadableEntries > 0),
            "a planning sample carrying the unreadable count", timeoutMs: 5_000);

        RunProgressEvent sample = h.Bus.Events.OfType<RunProgressEvent>().Last(e => e.UnreadableEntries > 0);
        Assert.Equal(4, sample.ScannedSources);
        Assert.Equal(3, sample.UnreadableEntries);

        planner.Release();
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
    }

    /// <summary>The stage, which is the one thing the counts cannot say. Between the walk ending and the
    /// sweep beginning, both counts sit frozen at their final values while the work list is written — from
    /// outside indistinguishable from a walk that has stopped dead, which is the same complaint one stage
    /// along. A source chunk is proof the walk is over; the sweep announces itself with its own marker.</summary>
    [Fact]
    public async Task The_stage_moves_from_scanning_through_building_to_sweeping()
    {
        StallingPlanner planner = new(sources: 5, destinations: 2);
        using RunPlanHarness h = new("run-plan-progress-stages") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;
        await RunPlanHarness.WaitUntilAsync(
            () => h.Bus.Events.OfType<RunProgressEvent>().Any(e => e.ScannedSources > 0),
            "the scanning sample", timeoutMs: 5_000);
        planner.Release();
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);

        // In order, and without assuming how many samples each stage produced — the stream is throttled and
        // lossy by contract, so only the sequence of DISTINCT stages is a property worth pinning.
        List<string> stages = [.. h.Bus.Events.OfType<RunProgressEvent>()
            .Where(e => e.Phase == nameof(RunPhase.Planning))
            .Select(e => e.PlanStage)
            .Distinct()];
        Assert.Equal(
            [RunPlanStages.Scanning, RunPlanStages.Building, RunPlanStages.Sweeping],
            stages);

        // The building sample is the one that carries the frozen counts, and it exists precisely so that
        // stretch is not silent.
        RunProgressEvent building = h.Bus.Events.OfType<RunProgressEvent>()
            .First(e => e.PlanStage == RunPlanStages.Building);
        Assert.Equal(5, building.ScannedSources);
        Assert.Equal(2, h.Bus.Events.OfType<RunProgressEvent>()
            .Where(e => e.PlanStage == RunPlanStages.Sweeping).Max(e => e.ScannedDestinations));
    }

    /// <summary>A scan that has found nothing says nothing. The poll fires ten times a second at every
    /// connected client, so a sample that repeats the last one is pure noise — and the run has already
    /// announced its zero the moment it began.</summary>
    [Fact]
    public async Task A_scan_that_has_found_nothing_does_not_republish_the_same_zero()
    {
        StallingPlanner planner = new(sources: 0);
        using RunPlanHarness h = new("run-plan-progress-quiet") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;
        await Task.Delay(600);   // six poll intervals of finding nothing

        // Exactly one: the announcement Begin made. Asserted while the planner is still parked, so nothing
        // else can have run in the meantime.
        Assert.Single(h.Bus.Events.OfType<RunProgressEvent>());

        planner.Release();
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
    }

    /// <summary>Quiet is not the same as mute. A stage whose counters are frozen — which the whole of
    /// Building is, by construction — still republishes at a floor.
    ///
    /// <para>Without it the last word on a plan can be a single sample nobody was connected for: a client
    /// that opens the queue window or reconciles mid-plan is seeded from <c>get-runs</c>, which carries no
    /// scan figures at all, and would read "starting the scan" until the run was planned. That is the exact
    /// complaint the live counts were added to answer, so the change gate must not reintroduce it.</para></summary>
    [Fact]
    public async Task A_frozen_stage_still_republishes_so_a_late_client_is_not_left_with_nothing()
    {
        StallingPlanner planner = new(sources: 9);
        using RunPlanHarness h = new("run-plan-progress-republish") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;
        await RunPlanHarness.WaitUntilAsync(
            () => h.Bus.Events.OfType<RunProgressEvent>().Any(e => e.ScannedSources > 0),
            "the scanning sample", timeoutMs: 5_000);

        // The counters cannot move again — the planner is parked and has finished bumping them — so a
        // SECOND sample carrying the same count can only have come from the republish floor.
        int soFar = h.Bus.Events.OfType<RunProgressEvent>().Count(e => e.ScannedSources == 9);
        await RunPlanHarness.WaitUntilAsync(
            () => h.Bus.Events.OfType<RunProgressEvent>().Count(e => e.ScannedSources == 9) > soFar,
            "the frozen count republished", timeoutMs: 10_000);

        planner.Release();
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
    }

    /// <summary>The ordering the whole shape of the fix exists to guarantee: a PLANNING sample must never be
    /// published after the run is planned, or a queue row would snap back from "33,120 files found" to
    /// "scanning…". Sampling on the run's own task — rather than from a background timer alongside it —
    /// makes that structural rather than something to be joined and hoped for. This test is what fails if
    /// anyone reintroduces a second publisher.</summary>
    [Fact]
    public async Task No_planning_sample_is_published_after_the_run_is_planned()
    {
        StallingPlanner planner = new(sources: 6, destinations: 2);
        using RunPlanHarness h = new("run-plan-progress-order") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;
        await RunPlanHarness.WaitUntilAsync(
            () => h.Bus.Events.OfType<RunProgressEvent>().Any(e => e.ScannedSources > 0),
            "at least one live planning sample", timeoutMs: 5_000);

        planner.Release();
        await RunPlanHarness.WaitForPhaseAsync(runs, handle!.RunId, RunPhase.AwaitingApproval);
        await Task.Delay(500);   // several poll intervals — a stray sampler would have had many chances

        List<EngineEvent> events = [.. h.Bus.Events];
        int planned = events.FindIndex(e => e is RunPlannedEvent);
        Assert.True(planned >= 0, "the run never published RunPlannedEvent");
        Assert.DoesNotContain(
            events.Skip(planned),
            e => e is RunProgressEvent { Phase: nameof(RunPhase.Planning) });
    }

    /// <summary>Driving the stream through an explicit enumerator must keep the cancellation contract the
    /// <c>await foreach</c> had: the run's token still reaches the planner, and the resulting cancellation
    /// still closes the run rather than surfacing as an unobserved fault on a detached task.</summary>
    [Fact]
    public async Task Cancelling_a_run_parked_in_its_first_advance_closes_it_as_cancelled()
    {
        StallingPlanner planner = new(sources: 3);
        using RunPlanHarness h = new("run-plan-progress-cancel") { Planner = planner };
        RunCoordinator runs = h.Coordinator();

        runs.Begin(h.AdditiveProfile(), null).TryGetValue(out RunHandle? handle);
        await planner.Walked;

        Assert.False(runs.Cancel(handle!.RunId).TryGetError(out _));
        RunStatus status = await RunPlanHarness.WaitForPhaseAsync(runs, handle.RunId, RunPhase.Closed);

        Assert.Equal(RunOutcome.Cancelled, status.Outcome);
        Assert.True(planner.Cancelled, "the run's token never reached the planner");
    }
}

/// <summary>A planner with the real one's SHAPE, which is the whole reason this bug existed: the entire
/// walk happens inside the first <c>MoveNextAsync</c> — the counters move, and no chunk exists — and only
/// then does the stream yield anything.
///
/// <para>Parked on a <see cref="TaskCompletionSource"/> rather than a sleep, so a test states when the walk
/// ends instead of racing it. Yields empty chunks: nothing here is about what a plan CONTAINS, and an empty
/// chunk is a shape production already produces (the sweep's phase marker is one).</para></summary>
internal sealed class StallingPlanner(int sources = 0, int unreadable = 0, int destinations = 0)
    : IProfilePlanner
{
    private static readonly DryRunChunk Empty = new([], [], [], []);

    private readonly TaskCompletionSource _walked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the counters have been bumped and the first advance is parked.</summary>
    public Task Walked => _walked.Task;

    /// <summary>Whether the caller's token reached us — the cancellation assertion.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Lets the "walk" finish and the chunks flow.</summary>
    public void Release() => _release.TrySetResult();

    public async IAsyncEnumerable<Result<PlanChunk, string>> PlanAsync(
        Profile profile, string? scopePath, PlanState state,
        DryRunProgressCounters? progress = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < sources; i++)
            progress?.SourceDiscovered();
        for (int i = 0; i < unreadable; i++)
            progress?.EntrySkipped();
        _walked.TrySetResult();

        try
        {
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }

        // The first chunk of the source phase: proof, to the consumer, that the walk is over.
        yield return new PlanChunk(Empty, PlanPhase.Sources, 0, 0);

        if (destinations <= 0)
            yield break;

        // The zero-entry marker the real planner uses to announce the sweep before it has found anything.
        yield return new PlanChunk(Empty, PlanPhase.Destinations, 0, 0, PhaseStarted: true);
        for (int i = 0; i < destinations; i++)
            progress?.DestinationDiscovered();
        yield return new PlanChunk(Empty, PlanPhase.Destinations, 0, 0);
    }
}
