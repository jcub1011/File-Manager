using FileManager.UI.Controls;
using Xunit;

namespace FileManager.UI.Tests;

public sealed class PathTruncationTests
{
    [Fact]
    public void Text_shorter_than_max_is_unchanged()
    {
        Assert.Equal("short.md", PathTruncation.TruncateEndPreservingExtension("short.md", 20));
        Assert.Equal("a/b/c", PathTruncation.TruncateMiddle("a/b/c", 20));
    }

    [Fact]
    public void End_truncation_keeps_the_extension()
    {
        string result = PathTruncation.TruncateEndPreservingExtension("LongFileName.md", 12);
        Assert.EndsWith("md", result);
        Assert.Contains('…', result);
        Assert.Equal(12, result.Length);
        Assert.StartsWith("LongFile", result);
        Assert.DoesNotContain(".md", result);   // the dot is dropped: "LongFileN…md"
    }

    [Fact]
    public void End_truncation_keeps_only_the_last_extension_on_multi_dot_names()
    {
        string result = PathTruncation.TruncateEndPreservingExtension("archive.tar.gz", 8);
        Assert.EndsWith("gz", result);
        Assert.DoesNotContain("tar", result);
        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void End_truncation_treats_a_dotfile_as_having_no_extension()
    {
        string result = PathTruncation.TruncateEndPreservingExtension(".gitignore", 6);
        Assert.Equal(6, result.Length);
        Assert.EndsWith("…", result);
        Assert.StartsWith(".giti", result);
    }

    [Fact]
    public void End_truncation_falls_back_to_middle_when_extension_leaves_no_room_for_the_stem()
    {
        // The extension alone is longer than the budget, so a stem+ellipsis+ext layout is impossible.
        string result = PathTruncation.TruncateEndPreservingExtension("f.verylongext", 5);
        Assert.Equal(5, result.Length);
        Assert.Contains('…', result);
    }

    [Fact]
    public void Middle_truncation_keeps_head_and_tail()
    {
        string result = PathTruncation.TruncateMiddle(@"C:\Long\Path\To\The\File", 12);
        Assert.Equal(12, result.Length);
        Assert.Contains('…', result);
        Assert.StartsWith("C:", result);
        Assert.EndsWith("File", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Tiny_budgets_do_not_throw(int maxChars)
    {
        string a = PathTruncation.TruncateMiddle("abcdef", maxChars);
        string b = PathTruncation.TruncateEndPreservingExtension("abc.def", maxChars);
        Assert.True(a.Length <= (maxChars <= 0 ? 0 : 1));
        Assert.True(b.Length <= (maxChars <= 0 ? 0 : 1));
    }
}
