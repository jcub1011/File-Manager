using FileManager.Core.Filtering;
using System.Text.RegularExpressions;

namespace FileManager.Core.Tests.Filtering;

public sealed class GlobTranslatorTests
{
    private static bool Matches(string glob, string relativePath) =>
        Regex.IsMatch(
            relativePath.Replace('\\', '/'),
            GlobTranslator.Translate(glob),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [Theory]
    // a pattern without '/' matches the file NAME at any depth
    [InlineData("*.wav", "song.wav", true)]
    [InlineData("*.wav", "nested/deep/song.wav", true)]
    [InlineData("*.wav", "song.wav.bak", false)]
    [InlineData("*.WAV", "song.wav", true)]                    // case-insensitive
    [InlineData("song?.txt", "song1.txt", true)]
    [InlineData("song?.txt", "song12.txt", false)]
    // '*' does not cross separators
    [InlineData("a*b", "a/b", false)]
    // a pattern with '/' anchors to the full relative path
    [InlineData("sub/*.txt", "sub/a.txt", true)]
    [InlineData("sub/*.txt", "other/sub/a.txt", false)]
    [InlineData("sub/*.txt", "sub/deeper/a.txt", false)]
    // '**' spans directories; '**/' also matches zero directories
    [InlineData("**/*.txt", "a.txt", true)]
    [InlineData("**/*.txt", "x/y/z/a.txt", true)]
    [InlineData("sub/**/*.txt", "sub/a/b/c.txt", true)]
    [InlineData("sub/**/*.txt", "sub/c.txt", true)]
    // literal regex metacharacters in names
    [InlineData("report (1)+[final].txt", "report (1)+[final].txt", true)]
    [InlineData("a[b].txt", "ab.txt", false)]                  // '[' is literal, not a class
    // backslash input patterns are normalized
    [InlineData(@"sub\*.txt", "sub/a.txt", true)]
    public void Glob_semantics(string glob, string relativePath, bool expected) =>
        Assert.Equal(expected, Matches(glob, relativePath));
}
