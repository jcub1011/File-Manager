using FileManager.Core.Jobs;
using FileManager.Core.Locking;

namespace FileManager.Core.Tests.Locking;

public sealed class PathLockRegistryTests
{
    private static NormalizedPath P(string path)
    {
        NormalizedPath.Create(path).TryGetValue(out NormalizedPath p);
        return p;
    }

    [Fact]
    public async Task Second_acquirer_waits_until_the_first_releases()
    {
        var registry = new PathLockRegistry();
        NormalizedPath path = P(@"C:\a\file.txt");

        PathLockSet first = await registry.AcquireAsync([path], JobId.New());
        ValueTask<PathLockSet> secondTask = registry.AcquireAsync([path], JobId.New());
        Assert.False(secondTask.IsCompleted);            // blocked while first holds it

        await first.DisposeAsync();
        PathLockSet second = await secondTask;           // now proceeds
        Assert.Contains(path, second.Paths);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Disjoint_paths_do_not_block()
    {
        var registry = new PathLockRegistry();
        await using PathLockSet a = await registry.AcquireAsync([P(@"C:\a")], JobId.New());
        await using PathLockSet b = await registry.AcquireAsync([P(@"C:\b")], JobId.New());
        Assert.Single(a.Paths);
        Assert.Single(b.Paths);
    }

    [Fact]
    public async Task Try_acquire_additional_is_nonblocking_and_reflects_contention()
    {
        var registry = new PathLockRegistry();
        NormalizedPath candidate = P(@"C:\a\name (1).txt");

        await using PathLockSet held = await registry.AcquireAsync([P(@"C:\a\name.txt")], JobId.New());
        Assert.True(registry.TryAcquireAdditional(held, candidate));   // free → reserved
        Assert.Contains(candidate, held.Paths);

        await using PathLockSet other = await registry.AcquireAsync([], JobId.New());
        Assert.False(registry.TryAcquireAdditional(other, candidate));  // held by the first set
    }

    [Fact]
    public async Task Cancelling_a_waiter_releases_it_without_stealing_the_lock()
    {
        var registry = new PathLockRegistry();
        NormalizedPath path = P(@"C:\a\file.txt");
        PathLockSet holder = await registry.AcquireAsync([path], JobId.New());

        using var cts = new CancellationTokenSource();
        ValueTask<PathLockSet> waiter = registry.AcquireAsync([path], JobId.New(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);

        // The lock is still cleanly releasable/re-acquirable after a cancelled waiter.
        await holder.DisposeAsync();
        await using PathLockSet next = await registry.AcquireAsync([path], JobId.New());
        Assert.Contains(path, next.Paths);
    }
}
