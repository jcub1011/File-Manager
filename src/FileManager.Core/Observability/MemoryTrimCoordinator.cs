using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;

namespace FileManager.Core.Observability;

/// <summary>Scope for one memory-heavy operation. Trims are suppressed while any scope is open, and
/// disposing it arms the debounce. Set <see cref="Units"/> as the operation progresses (files
/// touched) so a run that ends early still reports the work it actually did.</summary>
public interface IMemoryTrimScope : IDisposable
{
    long Units { get; set; }
}

/// <summary>Decides when the service should hand memory back to the OS after a burst.
///
/// <para>Reducing peak allocation does not reduce RSS on its own: the GC keeps its high-water commit,
/// and the Large Object Heap is not compacted by default. A dry run over a few hundred thousand files
/// leaves the process holding committed-but-free heap indefinitely, which is what a user sees in Task
/// Manager and reports as a leak. This asks for it back — but only when doing so is both worthwhile
/// and free of user-visible cost.</para></summary>
public interface IMemoryTrimCoordinator
{
    /// <summary>Opens a scope for one memory-heavy operation.</summary>
    IMemoryTrimScope BeginOperation();
}

/// <inheritdoc cref="IMemoryTrimCoordinator"/>
/// <remarks>Trims only when ALL four conditions hold, because an aggressive compacting gen2 suspends
/// every managed thread and each condition rules out a case where that cost buys nothing:
/// <list type="number">
/// <item>enough work has accumulated since the last trim — a ten-file preview never triggers one;</item>
/// <item>nothing has run for the quiet period — this is the debounce, so back-to-back dry runs pay
/// once at the end instead of once each;</item>
/// <item>there is something to reclaim — either committed-but-not-live is large (the classic
/// symptom: collected-but-not-decommitted heap), or total committed sits far above the idle floor
/// with the garbage simply not collected yet (the post-allocation-avoidance shape, where the run
/// triggers almost no collections). Both self-scale to what the run really cost;</item>
/// <item>no operation is in flight — mid-sweep, LOH compaction would be O(live LOH) with up to
/// hundreds of scan workers suspended behind it.</item>
/// </list></remarks>
public sealed class MemoryTrimCoordinator : IMemoryTrimCoordinator, IDisposable
{
    /// <summary>Work units (source files emitted + destination entries swept) that must accumulate
    /// before a trim is worth its pause. Roughly "a real run, not a preview of one folder".</summary>
    private const long WorkUnitsThreshold = 25_000;

    /// <summary>Committed-but-not-live bytes below which there is nothing worth reclaiming. This is
    /// the condition that actually describes the reported symptom.</summary>
    private const long CommittedSlackBytes = 32L * 1024 * 1024;

    /// <summary>Total committed bytes above which a trim is worth it even with LITTLE slack. Slack
    /// (committed − heap) only measures memory the GC has already collected but not decommitted; a
    /// low-allocation burst (post allocation-avoidance work, the dry run barely triggers collections)
    /// ends with the heap FULL of uncollected garbage instead — committed ≈ heap, near-zero slack,
    /// and yet one aggressive collect returns almost all of it. Discovered the hard way: after the
    /// sweep stopped allocating per-entry, a 500k-file dry run settled at ~200 MB because the slack
    /// gate alone read "nothing to reclaim". Well above the ~8 MB idle floor and comfortably above
    /// anything a small preview commits, so trivial runs still never pay.</summary>
    private const long CommittedFloorBytes = 96L * 1024 * 1024;

    /// <summary>How long the process must stay idle before a trim fires. Long enough that a user
    /// re-running a dry run, or tweaking a profile and previewing again, never pays for it.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(25);

    private readonly ISettingsProvider _settings;
    private readonly ILogger<MemoryTrimCoordinator> _logger;
    private readonly ITimer _timer;

    private long _pendingUnits;
    private int _inFlight;
    private volatile bool _disposed;

    /// <summary>Test seam: production compacts and collects. Tests substitute a recorder so the
    /// DECISION logic can be asserted without actually pausing the test host — and so a passing test
    /// means "it decided to trim", which is the part with branches in it.</summary>
    internal Action<long>? TrimOverride { get; init; }

    /// <summary>Test seam: production reads the live GC. Tests pin (committed, heap) so the
    /// committed-slack condition is deterministic rather than dependent on whatever the test host's
    /// heap happens to look like.</summary>
    internal Func<(long Committed, long Heap)>? MemoryProbeOverride { get; init; }

    public MemoryTrimCoordinator(
        ILogger<MemoryTrimCoordinator> logger, ISettingsProvider settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _logger = logger;
        _settings = settings;
        // One-shot, re-armed on every completed operation — re-arming IS the debounce. Created
        // disarmed so an idle service never wakes up.
        _timer = time.CreateTimer(_ => OnQuietPeriodElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public IMemoryTrimScope BeginOperation()
    {
        Interlocked.Increment(ref _inFlight);
        return new Scope(this);
    }

    private void EndOperation(long units)
    {
        Interlocked.Add(ref _pendingUnits, units);
        Interlocked.Decrement(ref _inFlight);
        ArmQuietTimer();
    }

    /// <summary>(Re)arms the quiet-period debounce. The disposed check alone is a race — a scope
    /// finishing while the service shuts down can dispose the timer between the check and the Change —
    /// and a scope's Dispose runs inside handler teardown, where a throw would surface as a handler
    /// fault. There is nothing left to trim at that point, so the losing side of the race is simply
    /// swallowed.</summary>
    private void ArmQuietTimer()
    {
        if (_disposed)
            return;
        try
        {
            _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // shutdown raced the re-arm; nothing left to trim
        }
    }

    private void OnQuietPeriodElapsed()
    {
        try
        {
            // Still busy: another operation started inside the quiet period. Re-arm rather than trim —
            // this is the case condition 4 exists for.
            if (Volatile.Read(ref _inFlight) > 0)
            {
                ArmQuietTimer();
                return;
            }

            if (!_settings.Current.ReleaseMemoryAfterLargeOperations)
                return;

            // Read without consuming: several small runs should be able to add up to the threshold
            // rather than each resetting it. Only an actual trim consumes the accumulated units.
            long units = Interlocked.Read(ref _pendingUnits);
            if (units < WorkUnitsThreshold)
                return;

            (long committed, long heap) = ReadMemory();
            long slack = committed - heap;
            // Two shapes of "worth reclaiming": committed-but-free heap the GC kept (slack), or a
            // heap still full of uncollected garbage after a low-allocation burst (high committed,
            // near-zero slack — see CommittedFloorBytes).
            if (slack < CommittedSlackBytes && committed < CommittedFloorBytes)
                return;

            Interlocked.Add(ref _pendingUnits, -units);
            if (TrimOverride is { } substitute)
                substitute(units);
            else
                Trim(units, slack, committed, heap);
        }
        catch (Exception ex)
        {
            // Last resort: this runs on a timer callback with nobody to observe it, and a memory
            // optimization must never be able to take the service down.
            _logger.LogWarning(ex, "Releasing memory after a large operation failed");
        }
    }

    private (long Committed, long Heap) ReadMemory()
    {
        if (MemoryProbeOverride is { } substitute)
            return substitute();
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        return (info.TotalCommittedBytes, info.HeapSizeBytes);
    }

    private void Trim(long units, long slackBefore, long committedBefore, long heapBefore)
    {
        Stopwatch watch = Stopwatch.StartNew();
        // TWO passes, deliberately. With LOH allocations a single aggressive collect is reported not to
        // decommit (dotnet/runtime#78679); smaller allocations settle in one. This service's frames were
        // almost all LOH before the chunk-budget fix, and a spilled snapshot replay still churns large
        // buffers, so the two-pass case is the one to assume. LargeObjectHeapCompactionMode reverts to
        // Default after every blocking GC, hence setting it inside the loop rather than once.
        //
        // The argument form is the only legal one: GCCollectionMode.Aggressive throws unless the
        // generation is MaxGeneration and both blocking and compacting are true.
        for (int pass = 0; pass < 2; pass++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        watch.Stop();

        GCMemoryInfo after = GC.GetGCMemoryInfo();
        _logger.LogInformation(
            "Released memory after {Units:N0} units of work in {ElapsedMs}ms: " +
            "GC committed {BeforeMb}MB -> {AfterMb}MB, heap {BeforeHeapMb}MB -> {AfterHeapMb}MB " +
            "(committed-but-free was {SlackMb}MB)",
            units, watch.ElapsedMilliseconds,
            committedBefore >> 20, after.TotalCommittedBytes >> 20,
            heapBefore >> 20, after.HeapSizeBytes >> 20,
            slackBefore >> 20);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private sealed class Scope(MemoryTrimCoordinator owner) : IMemoryTrimScope
    {
        private bool _ended;

        public long Units { get; set; }

        public void Dispose()
        {
            // Idempotent: an async iterator can be disposed more than once, and double-counting would
            // both inflate the units and corrupt the in-flight count (which gates the trim).
            if (_ended)
                return;
            _ended = true;
            owner.EndOperation(Units);
        }
    }
}

/// <summary>No-op coordinator for tests and any host that has not registered the real one: every
/// operation is tracked and immediately forgotten, and nothing ever collects.</summary>
public sealed class NullMemoryTrimCoordinator : IMemoryTrimCoordinator
{
    public static NullMemoryTrimCoordinator Instance { get; } = new();

    public IMemoryTrimScope BeginOperation() => new Scope();

    private sealed class Scope : IMemoryTrimScope
    {
        public long Units { get; set; }
        public void Dispose() { }
    }
}
