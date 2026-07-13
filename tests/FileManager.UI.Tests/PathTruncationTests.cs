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

    [Fact]
    public void Folder_truncation_elides_whole_middle_folders_keeping_root_and_parent()
    {
        // Long\Path\To\The\File\ (22) → drop the middle folders into one ellipsis, keeping root+parent
        // and every separator intact.
        Assert.Equal(@"Long\…\File\", PathTruncation.TruncateFolders(@"Long\Path\To\The\File\", 12));
    }

    [Fact]
    public void Folder_truncation_preserves_the_root_and_parent_names_and_drops_the_middle()
    {
        string result = PathTruncation.TruncateFolders(@"LongRoot\middleAlpha\middleBeta\ParentDir\file", 24);
        Assert.True(result.Length <= 24);
        Assert.StartsWith("LongRoot", result);
        Assert.Contains('…', result);
        Assert.DoesNotContain("middle", result);   // middle folders are the first to go
    }

    [Fact]
    public void Folder_truncation_never_partially_cuts_a_short_folder_name()
    {
        // Parent "ab" (≤ 3 chars) must survive whole; only the middle folders are elided to fit.
        string result = PathTruncation.TruncateFolders(@"LongRoot\mid1\mid2\ab", 14);
        Assert.True(result.Length <= 14);
        Assert.EndsWith("ab", result);
        Assert.DoesNotContain("mid", result);
        Assert.Contains('…', result);
    }

    [Fact]
    public void Folder_truncation_leaves_a_fitting_path_unchanged()
    {
        Assert.Equal(@"a\bb\ccc\", PathTruncation.TruncateFolders(@"a\bb\ccc\", 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(15)]
    public void Folder_truncation_never_exceeds_the_budget(int maxChars)
    {
        string result = PathTruncation.TruncateFolders(@"Alpha\Bravo\Charlie\Delta\Echo\file.txt", maxChars);
        Assert.True(result.Length <= maxChars);
    }

    // ── Surrogate-pair safety ───────────────────────────────────────────────────────────────────
    // Non-BMP characters (emoji, CJK ext-B) are two UTF-16 code units. A naive character cut can land
    // between the two halves, emitting a lone surrogate that renders as the replacement char (�).

    /// <summary>Fails if <paramref name="s"/> contains U+FFFD or any surrogate code unit without its
    /// pair — the exact corruption the TrimHead/TrimTailStart helpers exist to prevent.</summary>
    private static void AssertNoLoneSurrogates(string s)
    {
        Assert.DoesNotContain('�', s);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                Assert.True(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]),
                    $"lone high surrogate at index {i} in \"{s}\"");
                i++;   // the low half is accounted for
            }
            else
            {
                Assert.False(char.IsLowSurrogate(s[i]), $"lone low surrogate at index {i} in \"{s}\"");
            }
        }
    }

    private static string Emoji(int count) => string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", count));

    [Fact]
    public void Middle_truncation_never_splits_a_surrogate_pair()
    {
        string dir = Emoji(12);   // 12 emoji = 24 UTF-16 code units, every char part of a pair
        string result = PathTruncation.TruncateMiddle(dir, 11);

        Assert.True(result.Length <= 11);
        Assert.Contains('…', result);
        AssertNoLoneSurrogates(result);
    }

    [Fact]
    public void End_truncation_of_an_emoji_stem_keeps_the_extension_without_splitting_a_pair()
    {
        // Emoji stem + a plain extension; a small budget forces the stem cut to land mid-pair, where
        // TrimHead must back off to the pair boundary.
        string fileName = Emoji(5) + ".txt";
        string result = PathTruncation.TruncateEndPreservingExtension(fileName, 7);

        Assert.True(result.Length <= 7);
        Assert.EndsWith("txt", result);
        Assert.Contains('…', result);
        AssertNoLoneSurrogates(result);
    }

    [Fact]
    public void Folder_truncation_abbreviates_emoji_folder_names_without_splitting_a_pair()
    {
        // Two long emoji folder names must be abbreviated (no middle folders to elide with n == 2),
        // exercising the phase-2 TrimHead abbreviation on non-BMP segments.
        string path = Emoji(8) + @"\" + Emoji(8);
        string result = PathTruncation.TruncateFolders(path, 14);

        Assert.True(result.Length <= 14);
        Assert.Contains('…', result);
        AssertNoLoneSurrogates(result);
    }
}
