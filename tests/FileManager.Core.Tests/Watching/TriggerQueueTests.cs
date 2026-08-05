using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Watching;

public sealed class TriggerQueueTests
{
    private static Payload P(Guid profile, string source) =>
        new(profile, source, @"C:\src", TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Dequeues_in_fifo_order()
    {
        Guid profile = Guid.NewGuid();
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance);
        queue.Enqueue(P(profile, @"C:\src\a"));
        queue.Enqueue(P(profile, @"C:\src\b"));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await e.MoveNextAsync());
        Assert.Equal(@"C:\src\a", e.Current.SourcePath);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(@"C:\src\b", e.Current.SourcePath);
    }

    [Fact]
    public void Coalesces_duplicate_profile_and_source_while_pending()
    {
        Guid profile = Guid.NewGuid();
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance);
        queue.Enqueue(P(profile, @"C:\src\a"));
        queue.Enqueue(P(profile, @"C:\SRC\A"));   // same key (case-insensitive)

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public async Task Dequeue_gate_stays_shut_while_paused_and_releases_on_resume()
    {
        Guid profile = Guid.NewGuid();
        FakePauseState pause = new();
        pause.SetPaused(true);
        TriggerQueue queue = new(pause, NullLogger<TriggerQueue>.Instance);
        queue.Enqueue(P(profile, @"C:\src\a"));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> move = e.MoveNextAsync();
        await Task.Delay(100);
        Assert.False(move.IsCompleted, "the gate must be shut while paused");

        pause.SetPaused(false);
        Assert.True(await move);
        Assert.Equal(@"C:\src\a", e.Current.SourcePath);
    }

    // ── Per-run pause ───────────────────────────────────────────────────────────────────────────
    // The global pause above BLOCKS the dequeue outright. A per-run pause FILTERS instead: the consumer
    // walks past a paused run's entries and serves the next eligible one, so holding one run does not
    // stall every other. These pin that difference.

    private static Payload Run(Guid profile, string source, Guid runId) =>
        new(profile, source, @"C:\src", TriggerKind.ManualShell, DateTimeOffset.UnixEpoch, RunId: runId);

    [Fact]
    public async Task A_paused_runs_payloads_are_skipped_while_another_runs_are_served()
    {
        Guid profile = Guid.NewGuid();
        Guid held = Guid.NewGuid(), other = Guid.NewGuid();
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance, runPause);
        // The paused run is FIRST in FIFO order, which is the whole point: without the skip its entry
        // blocks the head of the queue and the second run never runs.
        queue.Enqueue(Run(profile, @"C:\src\held", held));
        queue.Enqueue(Run(profile, @"C:\src\other", other));
        runPause.Set(held, true);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await e.MoveNextAsync());
        Assert.Equal(@"C:\src\other", e.Current.SourcePath);
        // And the held payload is still pending, not dropped — it must run when resumed.
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.PendingCountForRun(held));
    }

    /// <summary>With nothing servable the consumer waits, exactly as it does on an empty queue — and a
    /// resume must WAKE it. Without the wake a resume takes effect only when some unrelated Enqueue
    /// happens to signal, which for the last run in the queue is never.</summary>
    [Fact]
    public async Task A_resume_wakes_a_consumer_parked_because_every_payload_was_paused()
    {
        Guid profile = Guid.NewGuid(), held = Guid.NewGuid();
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance, runPause);
        queue.Enqueue(Run(profile, @"C:\src\held", held));
        runPause.Set(held, true);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> move = e.MoveNextAsync();
        await Task.Delay(100);
        Assert.False(move.IsCompleted, "an all-paused queue must not serve anything");

        runPause.Set(held, false);
        Assert.True(await move);
        Assert.Equal(@"C:\src\held", e.Current.SourcePath);
    }

    /// <summary>A payload belonging to no run (a watcher or scheduler trigger) can never be withheld by a
    /// per-run pause — there is no run to pause.</summary>
    [Fact]
    public async Task A_payload_with_no_run_is_never_withheld()
    {
        Guid profile = Guid.NewGuid(), held = Guid.NewGuid();
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance, runPause);
        queue.Enqueue(Run(profile, @"C:\src\held", held));
        queue.Enqueue(P(profile, @"C:\src\loose"));
        runPause.Set(held, true);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await e.MoveNextAsync());
        Assert.Equal(@"C:\src\loose", e.Current.SourcePath);
    }

    /// <summary>Resuming must restore FIFO position, not send the held run to the back. Its entry never
    /// left the list, so a resumed run that was queued first is served first again.</summary>
    [Fact]
    public async Task A_resumed_run_keeps_its_place_in_the_queue()
    {
        Guid profile = Guid.NewGuid();
        Guid held = Guid.NewGuid(), other = Guid.NewGuid();
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance, runPause);
        queue.Enqueue(Run(profile, @"C:\src\held", held));
        queue.Enqueue(Run(profile, @"C:\src\a", other));
        queue.Enqueue(Run(profile, @"C:\src\b", other));
        runPause.Set(held, true);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(@"C:\src\a", e.Current.SourcePath);

        runPause.Set(held, false);
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(@"C:\src\held", e.Current.SourcePath);   // ahead of \b, which was queued after it
    }

    /// <summary>An unknown run reads as NOT paused. Load-bearing rather than lenient: a payload whose run
    /// the coordinator has already forgotten would otherwise be withheld forever.</summary>
    [Fact]
    public async Task A_payload_whose_run_is_unknown_is_served()
    {
        Guid profile = Guid.NewGuid();
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance, runPause);
        queue.Enqueue(Run(profile, @"C:\src\a", Guid.NewGuid()));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await e.MoveNextAsync());
    }

    /// <summary>The GLOBAL pause still blocks everything, whatever the per-run flags say — the two are
    /// independent and the global one is the bigger hammer.</summary>
    [Fact]
    public async Task The_global_pause_still_blocks_an_unpaused_run()
    {
        Guid profile = Guid.NewGuid();
        FakePauseState pause = new();
        pause.SetPaused(true);
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(pause, NullLogger<TriggerQueue>.Instance, runPause);
        queue.Enqueue(Run(profile, @"C:\src\a", Guid.NewGuid()));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<Payload> e = queue.DequeueAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> move = e.MoveNextAsync();
        await Task.Delay(100);

        Assert.False(move.IsCompleted);
        pause.SetPaused(false);
        Assert.True(await move);
    }

    /// <summary>Dropping a cancelled run's work still finds its payloads when they are paused — cancel must
    /// not be defeated by a hold the user forgot to release.</summary>
    [Fact]
    public void DropRun_removes_a_paused_runs_pending_payloads()
    {
        Guid profile = Guid.NewGuid(), held = Guid.NewGuid();
        RunPauseRegistry runPause = new(NullLogger<RunPauseRegistry>.Instance);
        TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance, runPause);
        queue.Enqueue(Run(profile, @"C:\src\a", held));
        queue.Enqueue(Run(profile, @"C:\src\b", held));
        runPause.Set(held, true);

        Assert.Equal(2, queue.DropRun(held));
        Assert.Equal(0, queue.PendingCount);
    }
}
