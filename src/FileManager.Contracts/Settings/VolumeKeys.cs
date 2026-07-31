namespace FileManager.Contracts.Settings;

/// <summary>The one definition of a volume key's canonical form, shared by the engine (which derives
/// keys from real paths) and the UI (which lets a user type or pick one). It lives in Contracts
/// because the UI references Contracts only — a copy on either side is how the two drifted apart.
///
/// Canonical form is lower-case with <em>no</em> trailing separator: <c>"c:"</c>,
/// <c>"\\server\share"</c>. That is the form already written to <c>settings.json</c> by the settings
/// UI and the form every doc comment and test in the repo assumes, so normalizing to it leaves
/// existing saved overrides working.
///
/// The trailing-separator trim is the load-bearing part. <see cref="System.IO.Path.GetPathRoot(string)"/>
/// returns <c>"C:\"</c> for a drive-letter path, and
/// <see cref="System.IO.Path.TrimEndingDirectorySeparator(string)"/> deliberately does NOT trim a path
/// that IS a root — so the obvious spelling yields <c>"c:\"</c>, which never matches a user-typed
/// <c>"c:"</c>. UNC roots come back without a trailing separator and are unaffected either way.</summary>
public static class VolumeKeys
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>The canonical key for <paramref name="key"/>: trimmed, lower-cased, and stripped of any
    /// trailing directory separators. Returns an empty string for null/blank input or for a key that is
    /// nothing but separators, so callers can treat "" as "no key" rather than guarding separately.</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";
        return key.Trim().ToLowerInvariant().TrimEnd(Separators);
    }
}
