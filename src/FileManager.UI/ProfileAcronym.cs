using System;
using System.Linq;

namespace FileManager.UI;

/// <summary>Derives a short (max two-letter) badge for the collapsed sidebar rail from a profile
/// name. Rules: multi-word names take the initials of the first two words ("First Profile" → "FP");
/// a single word takes its first two capitals so PascalCase reads sensibly ("SingleNameProfile" →
/// "SN"), falling back to the first two letters/digits when there aren't two capitals ("backup" →
/// "BA"). Blank names render "?".</summary>
public static class ProfileAcronym
{
    private const string Placeholder = "?";

    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Placeholder;

        string[] words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        string letters = words.Length >= 2
            // Multi-word: first character of each of the first two words.
            ? new string(words.Take(2).Select(w => w[0]).ToArray())
            // Single word: prefer its capitals (PascalCase/camelCase); otherwise its first chars.
            : FromSingleWord(words[0]);

        letters = letters.ToUpperInvariant();
        return letters.Length > 0 ? letters : Placeholder;
    }

    private static string FromSingleWord(string word)
    {
        char[] capitals = word.Where(char.IsUpper).Take(2).ToArray();
        if (capitals.Length >= 2)
            return new string(capitals);

        return new string(word.Where(char.IsLetterOrDigit).Take(2).ToArray());
    }
}
