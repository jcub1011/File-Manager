using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileManager.UI.ViewModels;

/// <summary>Display-path helpers for the dry-run panels: reduce a set of roots to the folder they all
/// share, and split an absolute path into a file name plus a directory shown relative to that shared
/// root. Both are pure and slash/case tolerant so they unit-test without a UI.</summary>
public static class DryRunPaths
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>The longest directory prefix shared by every root, or <c>null</c> when the roots span
    /// different drives / UNC shares (no shared prefix exists, so callers show full paths). A single
    /// root returns itself. Comparison is case-insensitive; both separator styles are accepted.</summary>
    public static string? CommonRoot(IEnumerable<string?> roots)
    {
        List<(string Head, string[] Segments)> parsed = [];
        foreach (string? raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (Parse(raw) is not { } p)
                return null;   // an unrecognizable root can't share a prefix with anything
            parsed.Add(p);
        }

        if (parsed.Count == 0)
            return null;

        string head = parsed[0].Head;
        foreach ((string Head, string[] _) entry in parsed)
        {
            if (!string.Equals(entry.Head, head, StringComparison.OrdinalIgnoreCase))
                return null;   // different drive or UNC share — no common root
        }

        int common = parsed[0].Segments.Length;
        for (int i = 1; i < parsed.Count; i++)
        {
            string[] segs = parsed[i].Segments;
            int n = Math.Min(common, segs.Length);
            int k = 0;
            while (k < n && string.Equals(parsed[0].Segments[k], segs[k], StringComparison.OrdinalIgnoreCase))
                k++;
            common = k;
        }

        string[] shared = parsed[0].Segments.Take(common).ToArray();
        return Reconstruct(head, shared);
    }

    /// <summary>Splits an absolute path into its file name and a directory portion displayed relative
    /// to <paramref name="commonRoot"/> (with a trailing separator). Falls back to the absolute
    /// directory when there is no common root or the path lies outside it.</summary>
    public static (string FileName, string ParentDisplay) SplitForDisplay(string absolutePath, string? commonRoot)
    {
        string fileName = Path.GetFileName(absolutePath);
        string dir = Path.GetDirectoryName(absolutePath) ?? "";

        string parent = dir;
        if (!string.IsNullOrEmpty(commonRoot) && dir.Length > 0)
        {
            string rel = Path.GetRelativePath(commonRoot, dir);
            if (rel == ".")
                rel = "";
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                parent = rel;
        }

        if (parent.Length > 0 && !EndsWithSeparator(parent))
            parent += Path.DirectorySeparatorChar;
        return (fileName, parent);
    }

    /// <summary>The path made relative to <paramref name="commonRoot"/> for building the tree forest,
    /// or the original path when there is no common root or the path lies outside it.</summary>
    public static string RelativeForTree(string path, string? commonRoot)
    {
        if (string.IsNullOrEmpty(commonRoot))
            return path;
        string rel = Path.GetRelativePath(commonRoot, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel;
    }

    private static (string Head, string[] Segments)? Parse(string raw)
    {
        string p = raw.Trim();

        // UNC: \\server\share[\...] — the minimal root is the share, so server AND share form the head.
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
        {
            string[] parts = p[2..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return null;   // incomplete UNC path
            string head = $@"\\{parts[0]}\{parts[1]}";
            return (head, parts.Skip(2).ToArray());
        }

        // Drive-rooted: X:\...
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            string rest = p.Length > 2 ? p[2..] : "";
            return ($"{char.ToUpperInvariant(p[0])}:", rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries));
        }

        return null;
    }

    private static string Reconstruct(string head, string[] segments)
    {
        // A drive head needs a trailing separator when nothing follows ("C:" → "C:\") so
        // Path.GetRelativePath treats it as the drive root; a UNC head is already a valid root.
        if (segments.Length == 0)
            return head.StartsWith(@"\\", StringComparison.Ordinal) ? head : head + Path.DirectorySeparatorChar;
        return head + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool EndsWithSeparator(string s) => s[^1] is '\\' or '/';
}
