using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Profiles;

/// <summary>Which active profiles' Sources match a given path (§4.1) — feeds the shell picker prompt
/// and CLI run validation (spec §3.2). A path matches a source when it equals or lies under the
/// source root; when the path is an existing file, the source's compiled filter must also accept it
/// so the picker never offers a profile that would filter the file out. Folder paths match on
/// containment alone (per-file filtering runs at job time).</summary>
public sealed class ProfileMatcher(
    IProfileCatalog catalog,
    IFilterCompiler filterCompiler,
    ILogger<ProfileMatcher> logger) : IProfileMatcher
{
    public IReadOnlyList<ProfileMatch> FindMatches(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return [];
        if (!NormalizedPath.Create(absolutePath).TryGetValue(out NormalizedPath target))
            return [];

        bool isFile = File.Exists(target.Value);
        List<ProfileMatch> matches = [];

        foreach (Profile profile in catalog.Active)
        {
            for (int i = 0; i < profile.Sources.Count; i++)
            {
                SourceConfig source = profile.Sources[i];
                if (!NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath root))
                    continue;

                if (target != root && !target.IsUnder(root))
                    continue;
                if (isFile && !Accepts(profile, source, root, target.Value))
                    continue;

                matches.Add(new ProfileMatch(profile.Id, profile.Name, root.Value));
                break;   // one entry per profile — the picker lists profiles, not source roots
            }
        }
        return matches;
    }

    private bool Accepts(Profile profile, SourceConfig source, NormalizedPath root, string filePath)
    {
        Result<CompiledFilterSet, string> compiled = filterCompiler.Compile(profile.Filters, source.Filters);
        if (!compiled.TryGetValue(out CompiledFilterSet? set) || set is null)
        {
            // A saved profile compiles (validation guarantees it); if it somehow does not, do not
            // hide the profile from a user who explicitly invoked this path.
            compiled.TryGetError(out string? error);
            logger.LogWarning("Profile {ProfileId} filter did not compile during match ({Error}); including it anyway", profile.Id, error);
            return true;
        }

        if (!FileMetadataReader.Read(filePath).TryGetValue(out FileMetadata? metadata) || metadata is null)
            return true;   // cannot stat — do not hide it

        string relativePath = Path.GetRelativePath(root.Value, filePath);
        int depth = SeparatorCount(relativePath);
        string? normalized = set.HasPatternRules ? NormalizeSeparators(relativePath) : null;
        FilterInput input = new(filePath, relativePath, depth, metadata, normalized);
        return set.Evaluate(in input).Matched;
    }

    private static int SeparatorCount(string value)
    {
        int count = 0;
        foreach (char c in value)
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                count++;
        return count;
    }

    private static string NormalizeSeparators(string relativePath) =>
        Path.DirectorySeparatorChar == '/' ? relativePath : relativePath.Replace('\\', '/');
}
