using FileManager.Core.Jobs;
using FileManager.Core.Placement;

namespace FileManager.Core.Tests.Placement;

public sealed class SourcePriorityRegistryTests
{
    private static NormalizedPath P(string path)
    {
        NormalizedPath.Create(path).TryGetValue(out NormalizedPath p);
        return p;
    }

    [Fact]
    public void Records_and_reads_back_a_placement()
    {
        var registry = new SourcePriorityRegistry();
        Guid profile = Guid.NewGuid();
        registry.RecordPlacement(profile, P(@"C:\t\f.txt"), 2);

        Assert.True(registry.TryGetPriority(profile, P(@"C:\t\f.txt"), out int index));
        Assert.Equal(2, index);
    }

    [Fact]
    public void Keeps_the_highest_priority_lowest_index()
    {
        var registry = new SourcePriorityRegistry();
        Guid profile = Guid.NewGuid();
        registry.RecordPlacement(profile, P(@"C:\t\f.txt"), 3);
        registry.RecordPlacement(profile, P(@"C:\t\f.txt"), 1);   // higher priority wins
        registry.RecordPlacement(profile, P(@"C:\t\f.txt"), 2);

        registry.TryGetPriority(profile, P(@"C:\t\f.txt"), out int index);
        Assert.Equal(1, index);
    }

    [Fact]
    public void Is_scoped_per_profile()
    {
        var registry = new SourcePriorityRegistry();
        registry.RecordPlacement(Guid.NewGuid(), P(@"C:\t\f.txt"), 0);
        Assert.False(registry.TryGetPriority(Guid.NewGuid(), P(@"C:\t\f.txt"), out _));
    }
}
