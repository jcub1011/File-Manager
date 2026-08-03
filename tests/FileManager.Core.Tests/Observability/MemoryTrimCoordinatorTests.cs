using FileManager.Contracts.Settings;
using FileManager.Core.Observability;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Observability;

/// <summary>The decision half of <see cref="MemoryTrimCoordinator"/>: WHEN it asks the GC to hand
/// memory back. The collection itself is substituted out — an aggressive compacting gen2 would pause
/// the test host for no assertable gain, and every branch worth testing is in the four conditions
/// guarding it, not in the two lines that call <c>GC.Collect</c>.
///
/// Each condition exists to rule out a case where a blocking, thread-suspending collection buys
/// nothing, so each gets a test that it actually blocks one.</summary>
public sealed class MemoryTrimCoordinatorTests
{
    private const long BigRun = 40_000;          // comfortably over the 25,000-unit threshold
    private const long SmallRun = 100;
    private static readonly TimeSpan PastQuietPeriod = TimeSpan.FromSeconds(30);   // quiet period is 25s

    /// <summary>Plenty of committed-but-free memory, so the slack condition is satisfied and the other
    /// three are what the test is actually exercising.</summary>
    private static (long, long) AbundantSlack() => (512L * 1024 * 1024, 64L * 1024 * 1024);

    private static (MemoryTrimCoordinator Coordinator, List<long> Trims) NewCoordinator(
        FakeTimeProvider time, bool enabled = true, Func<(long, long)>? memory = null)
    {
        List<long> trims = [];
        GlobalSettings settings = new() { ReleaseMemoryAfterLargeOperations = enabled };
        MemoryTrimCoordinator coordinator = new(
            NullLogger<MemoryTrimCoordinator>.Instance, new FakeSettingsProvider(settings), time)
        {
            TrimOverride = trims.Add,
            MemoryProbeOverride = memory ?? AbundantSlack,
        };
        return (coordinator, trims);
    }

    private static void RunOperation(MemoryTrimCoordinator coordinator, long units)
    {
        using IMemoryTrimScope scope = coordinator.BeginOperation();
        scope.Units = units;
    }

    [Fact]
    public void Trims_once_a_large_run_has_been_idle_for_the_quiet_period()
    {
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, BigRun);
        Assert.Empty(trims);                    // not immediately — the run just finished

        time.Advance(PastQuietPeriod);

        Assert.Equal([BigRun], trims);
    }

    [Fact]
    public void Does_not_trim_before_the_quiet_period_elapses()
    {
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, BigRun);
        time.Advance(TimeSpan.FromSeconds(20));   // still inside the 25s window

        Assert.Empty(trims);
    }

    [Fact]
    public void Back_to_back_runs_debounce_into_a_single_trim()
    {
        // The motivating case: a user tweaking a profile and re-previewing should not pay for a
        // blocking collection between every attempt.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        for (int i = 0; i < 4; i++)
        {
            RunOperation(coordinator, BigRun);
            time.Advance(TimeSpan.FromSeconds(10));   // each re-arms the debounce
        }
        Assert.Empty(trims);

        time.Advance(PastQuietPeriod);

        // One trim, carrying every run's accumulated work.
        Assert.Equal([BigRun * 4], trims);
    }

    [Fact]
    public void Does_not_trim_for_work_below_the_threshold()
    {
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, SmallRun);
        time.Advance(PastQuietPeriod);

        Assert.Empty(trims);
    }

    [Fact]
    public void Small_runs_accumulate_toward_the_threshold_instead_of_resetting_it()
    {
        // Units are only consumed by an actual trim, so a service doing steady small previews still
        // eventually reclaims. Checking the timer did not silently discard them.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        for (int i = 0; i < 3; i++)
        {
            RunOperation(coordinator, 10_000);
            time.Advance(PastQuietPeriod);
        }

        // 10k (no), 20k (no), 30k (crosses 25,000) — one trim carrying the full accumulated total.
        Assert.Equal([30_000L], trims);
    }

    [Fact]
    public void Does_not_trim_while_an_operation_is_still_in_flight()
    {
        // A trim mid-run would suspend every managed thread — including the scan workers — with LOH
        // compaction proportional to the live set. This is the condition that prevents it.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, BigRun);            // arms the debounce
        IMemoryTrimScope inFlight = coordinator.BeginOperation();   // a second run starts
        time.Advance(PastQuietPeriod);

        Assert.Empty(trims);

        // Once it finishes, the re-armed timer fires and the trim happens.
        inFlight.Units = BigRun;
        inFlight.Dispose();
        time.Advance(PastQuietPeriod);

        Assert.Equal([BigRun * 2], trims);
    }

    [Fact]
    public void Does_not_trim_when_committed_memory_is_not_meaningfully_above_the_live_heap()
    {
        // Committed-but-free IS the symptom; with none of it there is nothing to reclaim and the pause
        // would be pure cost. 8 MB of slack, against the 32 MB gate.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(
            time, memory: () => (72L * 1024 * 1024, 64L * 1024 * 1024));
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, BigRun);
        time.Advance(PastQuietPeriod);

        Assert.Empty(trims);
    }

    [Fact]
    public void Trims_when_committed_is_high_even_with_near_zero_slack()
    {
        // The post-allocation-avoidance shape: a big run that triggered almost no collections ends
        // with the heap FULL of uncollected garbage — committed ≈ heap, near-zero slack — yet one
        // aggressive collect returns almost all of it. The slack gate alone read this as "nothing to
        // reclaim" and left a 500k-file dry run settled at ~200 MB; the committed floor is what
        // catches it. 200 MB committed, 4 MB slack.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(
            time, memory: () => (200L * 1024 * 1024, 196L * 1024 * 1024));
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, BigRun);
        time.Advance(PastQuietPeriod);

        Assert.Equal([BigRun], trims);
    }

    [Fact]
    public void Does_not_trim_when_the_setting_is_off()
    {
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time, enabled: false);
        using MemoryTrimCoordinator _ = coordinator;

        RunOperation(coordinator, BigRun);
        time.Advance(PastQuietPeriod);

        Assert.Empty(trims);
    }

    [Fact]
    public void Disposing_a_scope_twice_does_not_double_count_or_corrupt_the_in_flight_gate()
    {
        // An async iterator can be disposed more than once. A double decrement would drive the
        // in-flight count negative and let a trim run during a later operation.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        IMemoryTrimScope scope = coordinator.BeginOperation();
        scope.Units = BigRun;
        scope.Dispose();
        scope.Dispose();

        time.Advance(PastQuietPeriod);
        Assert.Equal([BigRun], trims);   // counted once, not twice

        // And the gate still holds for a genuinely concurrent operation.
        using IMemoryTrimScope stillRunning = coordinator.BeginOperation();
        RunOperation(coordinator, BigRun);
        time.Advance(PastQuietPeriod);
        Assert.Single(trims);
    }

    [Fact]
    public void A_scope_finishing_after_the_coordinator_is_disposed_does_not_throw()
    {
        // The shutdown race: a run's trim scope is disposed inside handler teardown while the DI
        // container is disposing the coordinator (and with it the timer). Re-arming then must be a
        // silent no-op — a scope's Dispose runs in teardown paths where a throw would surface as a
        // handler fault, and there is nothing left to trim anyway.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);

        IMemoryTrimScope scope = coordinator.BeginOperation();
        scope.Units = BigRun;
        coordinator.Dispose();

        scope.Dispose();   // must not throw

        time.Advance(PastQuietPeriod);
        Assert.Empty(trims);
    }

    [Fact]
    public void An_idle_coordinator_never_trims()
    {
        // Nothing has run, so the timer must not even be armed — an always-on background service
        // should not wake up to do GC work it has no reason to do.
        FakeTimeProvider time = new();
        (MemoryTrimCoordinator coordinator, List<long> trims) = NewCoordinator(time);
        using MemoryTrimCoordinator _ = coordinator;

        time.Advance(TimeSpan.FromHours(1));

        Assert.Empty(trims);
    }
}
