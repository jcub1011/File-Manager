using System;
using System.IO;

namespace FileManager.UI.Controls;

/// <summary>How <see cref="PathText"/> shortens a string that is too wide for its column.</summary>
public enum PathTruncationMode
{
    /// <summary>Keep the head and tail, drop the middle: <c>C:\Long\Pa…\File\</c> — for directory paths.</summary>
    MiddleEllipsis,

    /// <summary>Keep the file extension, truncate the stem: <c>LongFileNa…md</c> — for file names.</summary>
    EndPreservingExtension,
}

/// <summary>Character-count based path shortening. Kept UI-free (no Avalonia types) so the branching
/// is exercised by plain unit tests; <see cref="PathText"/> only supplies the pixel→char conversion
/// (trivial because the paths render in a monospaced font, so every glyph is one column wide).</summary>
public static class PathTruncation
{
    private const char Ellipsis = '…';

    /// <summary>Shortens <paramref name="text"/> to at most <paramref name="maxChars"/> characters,
    /// keeping the head and tail and replacing the middle with an ellipsis.</summary>
    public static string TruncateMiddle(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;
        if (maxChars <= 0)
            return "";
        if (maxChars == 1)
            return Ellipsis.ToString();

        int budget = maxChars - 1;          // one column is spent on the ellipsis
        int head = budget / 2;
        int tail = budget - head;
        return string.Concat(text.AsSpan(0, head), Ellipsis.ToString(), text.AsSpan(text.Length - tail, tail));
    }

    /// <summary>Shortens a file name to at most <paramref name="maxChars"/> characters, truncating the
    /// stem from the end but keeping the extension so the type stays legible
    /// (<c>LongFileName.md</c> → <c>LongFileNa…md</c>). Falls back to a middle-ellipsis when the
    /// extension alone would not leave room for at least one stem character.</summary>
    public static string TruncateEndPreservingExtension(string fileName, int maxChars)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length <= maxChars)
            return fileName;
        if (maxChars <= 0)
            return "";
        if (maxChars == 1)
            return Ellipsis.ToString();

        // A leading dot (".gitignore") or a trailing dot ("name.") is not a usable extension.
        int dot = fileName.LastIndexOf('.');
        bool hasExtension = dot > 0 && dot < fileName.Length - 1;
        if (!hasExtension)
            return string.Concat(fileName.AsSpan(0, maxChars - 1), Ellipsis.ToString());

        string extNoDot = fileName[(dot + 1)..];
        int keep = maxChars - 1 - extNoDot.Length;   // ellipsis + extension consume the rest
        if (keep < 1)
            return TruncateMiddle(fileName, maxChars);

        return string.Concat(fileName.AsSpan(0, Math.Min(keep, dot)), Ellipsis.ToString(), extNoDot);
    }
}
