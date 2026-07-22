using System.Text;
using System.Text.RegularExpressions;

namespace FileManager.Core.Filtering;

/// <summary>Translates a glob pattern into an anchored regex over the '/'-normalized relative
/// path. Semantics: '**' spans separators ('**/' also matches zero directories); '*' and '?'
/// stop at separators; a pattern containing no '/' matches the file NAME at any depth. '[' is
/// literal — bracket classes are not part of v1's glob dialect.</summary>
internal static class GlobTranslator
{
    public static string Translate(string glob)
    {
        string normalized = glob.Replace('\\', '/');
        StringBuilder pattern = new();
        pattern.Append(normalized.Contains('/') ? "^" : "^(?:.*/)?");

        int i = 0;
        while (i < normalized.Length)
        {
            char c = normalized[i];
            if (c == '*')
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    if (i + 2 < normalized.Length && normalized[i + 2] == '/')
                    {
                        pattern.Append("(?:.*/)?");   // "**/" spans zero or more directories
                        i += 3;
                    }
                    else
                    {
                        pattern.Append(".*");
                        i += 2;
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                    i++;
                }
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
                i++;
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        pattern.Append('$');
        return pattern.ToString();
    }
}
