using FileManager.Core.Jobs;

namespace FileManager.Core.Tests.Jobs;

public sealed class NormalizedPathTests
{
    [Theory]
    [InlineData(@"\\?\C:\data", @"C:\data")]
    [InlineData(@"\\.\C:\data", @"C:\data")]
    [InlineData(@"\\?\UNC\server\share\data", @"\\server\share\data")]
    public void Extended_length_prefixes_normalize_to_the_plain_form(string aliased, string plain)
    {
        // One physical location must have ONE key: path locks, session priorities, and self-write
        // suppression all treat NormalizedPath equality as file identity.
        Assert.True(NormalizedPath.Create(aliased).TryGetValue(out NormalizedPath a));
        Assert.True(NormalizedPath.Create(plain).TryGetValue(out NormalizedPath b));
        Assert.Equal(b, a);
    }

    [Fact]
    public void Case_differences_are_the_same_key_on_windows()
    {
        Assert.True(NormalizedPath.Create(@"C:\Data\File.txt").TryGetValue(out NormalizedPath a));
        Assert.True(NormalizedPath.Create(@"c:\DATA\file.TXT").TryGetValue(out NormalizedPath b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Relative_paths_are_rejected() =>
        Assert.False(NormalizedPath.Create(@"relative\path").TryGetValue(out _));
}
