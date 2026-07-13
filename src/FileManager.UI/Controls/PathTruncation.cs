using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileManager.UI.Controls;

/// <summary>How <see cref="PathText"/> shortens a string that is too wide for its column.</summary>
public enum PathTruncationMode
{
    /// <summary>Keep the head and tail, drop the middle: <c>C:\Long\Pa…\File\</c> — for directory paths.</summary>
    MiddleEllipsis,

    /// <summary>Keep the file extension, truncate the stem: <c>LongFileNa…md</c> — for file names.</summary>
    EndPreservingExtension,

    /// <summary>Middle-ellipsis at folder boundaries: never cut a separator; drop whole middle folders
    /// first (keeping the root and the file's parent folder); only abbreviate a folder name as a last
    /// resort, and never one ≤ 3 chars. <c>Long\Path\To\The\File\</c> → <c>Long\…\File\</c>.</summary>
    FolderMiddleEllipsis,
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

    /// <summary>Shortens a directory path to at most <paramref name="maxChars"/> characters at folder
    /// boundaries: separators (<c>\</c> <c>/</c>) are never cut, whole middle folders are dropped first
    /// (coalescing into a single ellipsis and keeping the root folder and the file's parent folder),
    /// and a surviving folder name is only abbreviated as a last resort — never one ≤ 3 chars.
    /// <c>Long\Path\To\The\File\</c> → <c>Long\…\File\</c>.</summary>
    public static string TruncateFolders(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;
        if (maxChars <= 1)
            return TruncateMiddle(text, maxChars);

        (string leadingSep, string[] segments, string[] sepAfter) = Tokenize(text);
        int n = segments.Length;
        if (n == 0)
            return TruncateMiddle(text, maxChars);   // path is all separators — nothing to elide

        string[] display = (string[])segments.Clone();
        bool[] kept = new bool[n];
        Array.Fill(kept, true);

        // Phase 1 — elide whole middle folders (indices 1..n-2), closest to the centre first, so the
        // root (0) and parent (n-1) survive longest. A run of elided folders renders as one ellipsis.
        if (n > 2)
        {
            double centre = (n - 1) / 2.0;
            List<int> middle = [];
            for (int i = 1; i <= n - 2; i++)
                middle.Add(i);
            middle.Sort((a, b) =>
            {
                int byCentre = Math.Abs(a - centre).CompareTo(Math.Abs(b - centre));
                return byCentre != 0 ? byCentre : a.CompareTo(b);
            });

            foreach (int idx in middle)
            {
                if (Build(leadingSep, display, sepAfter, kept).Length <= maxChars)
                    break;
                kept[idx] = false;
            }
        }

        string built = Build(leadingSep, display, sepAfter, kept);
        if (built.Length <= maxChars)
            return built;

        // Phase 2 — abbreviate the surviving parent, then the root (skip names ≤ 3 chars).
        foreach (int idx in new[] { LastKept(kept), FirstKept(kept) })
        {
            if (idx < 0 || segments[idx].Length <= 3)
                continue;
            for (int stem = segments[idx].Length - 2; stem >= 1; stem--)
            {
                display[idx] = segments[idx][..stem] + Ellipsis;
                built = Build(leadingSep, display, sepAfter, kept);
                if (built.Length <= maxChars)
                    return built;
            }
        }

        // Phase 3 — even root+parent won't fit; fall back to a hard character cut so we never overflow.
        return built.Length <= maxChars ? built : TruncateMiddle(text, maxChars);
    }

    /// <summary>Splits a path into its leading separators, its folder-name segments, and the separator
    /// run following each segment (the final entry is the trailing separator, or "").</summary>
    private static (string LeadingSep, string[] Segments, string[] SepAfter) Tokenize(string text)
    {
        int i = 0;
        int lead = 0;
        while (lead < text.Length && IsSep(text[lead]))
            lead++;
        string leadingSep = text[..lead];
        i = lead;

        List<string> segments = [];
        List<string> sepAfter = [];
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && !IsSep(text[i]))
                i++;
            segments.Add(text[start..i]);

            int sepStart = i;
            while (i < text.Length && IsSep(text[i]))
                i++;
            sepAfter.Add(text[sepStart..i]);
        }
        return (leadingSep, segments.ToArray(), sepAfter.ToArray());
    }

    /// <summary>Reconstructs the path from the kept segments, replacing each maximal run of dropped
    /// segments with a single ellipsis and preserving the original separators around it.</summary>
    private static string Build(string leadingSep, string[] display, string[] sepAfter, bool[] kept)
    {
        StringBuilder sb = new();
        sb.Append(leadingSep);
        int i = 0;
        while (i < display.Length)
        {
            if (kept[i])
            {
                sb.Append(display[i]);
                sb.Append(sepAfter[i]);
                i++;
            }
            else
            {
                int j = i;
                while (j < display.Length && !kept[j])
                    j++;
                sb.Append(Ellipsis);
                sb.Append(sepAfter[j - 1]);   // one separator on the far side of the elided run
                i = j;
            }
        }
        return sb.ToString();
    }

    private static int FirstKept(bool[] kept)
    {
        for (int i = 0; i < kept.Length; i++)
            if (kept[i]) return i;
        return -1;
    }

    private static int LastKept(bool[] kept)
    {
        for (int i = kept.Length - 1; i >= 0; i--)
            if (kept[i]) return i;
        return -1;
    }

    private static bool IsSep(char c) => c is '\\' or '/';
}
