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

    [Fact]
    public async Task Overlapping_multi_path_acquire_is_handed_over_only_after_the_first_releases()
    {
        var registry = new PathLockRegistry();
        NormalizedPath a = P(@"C:\a\1.txt");
        NormalizedPath b = P(@"C:\a\2.txt");
        NormalizedPath c = P(@"C:\a\3.txt");

        // First job holds {a, b}; the paths are acquired in ordinal order regardless of argument order.
        PathLockSet first = await registry.AcquireAsync([b, a], JobId.New());

        // Second job wants {b, c} — it overlaps on b, so it blocks until the first set is disposed.
        ValueTask<PathLockSet> secondTask = registry.AcquireAsync([c, b], JobId.New());
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        PathLockSet second = await secondTask;               // ownership handed over on release
        Assert.Contains(b, second.Paths);
        Assert.Contains(c, second.Paths);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Precancelled_token_throws_for_an_uncontended_path_and_leaves_no_lock_held()
    {
        var registry = new PathLockRegistry();
        NormalizedPath path = P(@"C:\a\free.txt");   // uncontended — nobody holds it

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await registry.AcquireAsync([path], JobId.New(), cts.Token));

        // The cancelled acquire reserved nothing, so another owner takes the path immediately.
        ValueTask<PathLockSet> follow = registry.AcquireAsync([path], JobId.New());
        Assert.True(follow.IsCompleted);
        await using PathLockSet next = await follow;
        Assert.Contains(path, next.Paths);
    }

    [Fact]
    public async Task TryAcquireAdditional_is_idempotent_for_a_path_the_set_already_holds()
    {
        // A job's lock set already contains every prospective final path, so a RenameSuffix probe of
        // the desired name asks for a lock the probing job itself owns. Reporting that as "taken"
        // made RenameSuffix skip the free desired name — and, with the desired name left empty,
        // every re-delivery suffixed again and grew the target set without bound.
        var registry = new PathLockRegistry();
        NormalizedPath path = P(@"C:\fm\target\doc.txt");
        await using PathLockSet held = await registry.AcquireAsync([path], JobId.New());

        Assert.True(registry.TryAcquireAdditional(held, path));
        // Recorded once, not twice: a duplicate would be released twice on dispose and could hand the
        // path to a waiter while this job still believed it held it.
        Assert.Single(held.Paths, p => p == path);
    }

    [Fact]
    public async Task A_path_another_job_holds_is_still_refused()
    {
        var registry = new PathLockRegistry();
        NormalizedPath path = P(@"C:\fm\target\doc.txt");
        await using PathLockSet mine = await registry.AcquireAsync([P(@"C:\fm\target\other.txt")], JobId.New());
        await using PathLockSet theirs = await registry.AcquireAsync([path], JobId.New());

        Assert.False(registry.TryAcquireAdditional(mine, path));
    }

    [Fact]
    public async Task An_idempotent_re_acquire_still_releases_the_path_exactly_once()
    {
        var registry = new PathLockRegistry();
        NormalizedPath path = P(@"C:\fm\target\doc.txt");
        PathLockSet held = await registry.AcquireAsync([path], JobId.New());
        Assert.True(registry.TryAcquireAdditional(held, path));

        await held.DisposeAsync();

        // Fully released: a fresh job takes it without waiting.
        ValueTask<PathLockSet> follow = registry.AcquireAsync([path], JobId.New());
        Assert.True(follow.IsCompleted);
        await using PathLockSet next = await follow;
        Assert.Contains(path, next.Paths);
    }
}
