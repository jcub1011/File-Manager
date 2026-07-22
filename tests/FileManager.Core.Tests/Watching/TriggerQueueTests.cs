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
}
