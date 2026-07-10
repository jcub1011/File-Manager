using FileManager.Core.Jobs;
using FileManager.Core.Locking;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Locking;

public sealed class SelfWriteSuppressionRegistryTests
{
    private static NormalizedPath P(string path)
    {
        NormalizedPath.Create(path).TryGetValue(out NormalizedPath p);
        return p;
    }

    [Fact]
    public void Registered_path_is_suppressed_and_others_are_not()
    {
        using var registry = new SelfWriteSuppressionRegistry(new FakeTimeProvider());
        registry.Register(P(@"C:\a\x.tmp"), JobId.New());

        Assert.True(registry.IsSuppressed(P(@"C:\a\x.tmp")));
        Assert.False(registry.IsSuppressed(P(@"C:\a\y.tmp")));
    }

    [Fact]
    public void Suppression_lingers_after_release_then_expires()
    {
        var time = new FakeTimeProvider();
        using var registry = new SelfWriteSuppressionRegistry(time);
        NormalizedPath path = P(@"C:\a\x.tmp");
        SuppressionToken token = registry.Register(path, JobId.New());

        token.Release(TimeSpan.FromSeconds(2));
        Assert.True(registry.IsSuppressed(path));            // still within the linger window

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(registry.IsSuppressed(path));

        time.Advance(TimeSpan.FromSeconds(2));               // past the 2s window
        Assert.False(registry.IsSuppressed(path));
    }

    [Fact]
    public void A_later_reregistration_is_not_lingered_by_a_stale_token()
    {
        var time = new FakeTimeProvider();
        using var registry = new SelfWriteSuppressionRegistry(time);
        NormalizedPath path = P(@"C:\a\x.tmp");

        SuppressionToken first = registry.Register(path, new JobId(Guid.NewGuid()));
        registry.Register(path, new JobId(Guid.NewGuid()));  // a second job re-registers the same path

        first.Release(TimeSpan.FromSeconds(2));              // stale token must not start the linger
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(registry.IsSuppressed(path));            // still actively suppressed by the second job
    }
}
