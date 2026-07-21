using FileManager.UI;

namespace FileManager.UI.Tests;

/// <summary>Verifies the two-letter badge derivation for the collapsed sidebar rail
/// (see <see cref="ProfileAcronym"/>).</summary>
public sealed class ProfileAcronymTests
{
    [Theory]
    [InlineData("First Profile", "FP")]        // multi-word: initials of the first two words
    [InlineData("SingleNameProfile", "SN")]    // single PascalCase word: first two capitals
    [InlineData("backup", "BA")]               // single lowercase word: first two letters
    [InlineData("my backup profile", "MB")]    // three words: still just the first two initials
    [InlineData("a", "A")]                      // single character
    [InlineData("  Padded  Name  ", "PN")]     // surrounding/interior whitespace ignored
    public void From_derives_the_expected_acronym(string name, string expected)
    {
        Assert.Equal(expected, ProfileAcronym.From(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_returns_placeholder_for_blank_names(string? name)
    {
        Assert.Equal("?", ProfileAcronym.From(name));
    }
}
