using FileManager.Contracts.Profiles;
using FileManager.Contracts.Primitives;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Watching;

/// <summary>The single enumeration path turning a profile (or one scoped root/file) into
/// candidate Payloads (§4.2): explicit-stack DFS over IFileSystemService, honoring the merged
/// MaxDepth and the unconditional infrastructure exclusions (I-INFRA-EXCLUDED).</summary>
public sealed class SourceScanner(
    ILogger<SourceScanner> logger, IFileSystemService fileSystem, TimeProvider time) : ISourceScanner
{
    private static readonly string[] InfrastructureDirectories = [".pipeline_tmp", ".fm_staging"];
    private const string TempFileMarker = ".fmtmp-";

    public IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null)
    {
        NormalizedPath? scope = null;
        if (scopeRoot is not null)
        {
            var scopeResult = NormalizedPath.Create(scopeRoot);
            if (scopeResult.TryGetError(out JobError? scopeError))
            {
                yield return new EnumerationFault($"scope path invalid: {scopeError.Message}", EnumerationSeverity.Fatal);
                yield break;
            }
            scopeResult.TryGetValue(out NormalizedPath scopeValue);
            scope = scopeValue;
        }

        bool scopeMatchedAnySource = false;
        foreach (SourceConfig source in profile.Sources)
        {
            if (!NormalizedPath.Create(source.Path).TryGetValue(out NormalizedPath sourceRoot))
                continue;   // saved profiles are validated; an unparseable root has no scannable content

            string walkRoot = sourceRoot.Value;
            if (scope is NormalizedPath scoped)
            {
                if (!scoped.Equals(sourceRoot) && !scoped.IsUnder(sourceRoot))
                    continue;
                scopeMatchedAnySource = true;
                if (File.Exists(scoped.Value))
                {
                    // A file scope yields exactly one payload (infra exclusions still apply).
                    if (!IsInfrastructurePath(scoped.Value))
                        yield return new Payload(profile.Id, scoped.Value, sourceRoot.Value, trigger, time.GetUtcNow());
                    continue;
                }
                walkRoot = scoped.Value;
            }

            int? maxDepth = source.Filters?.MaxDepth ?? profile.Filters?.MaxDepth;
            foreach (var item in Walk(profile.Id, sourceRoot.Value, walkRoot, maxDepth, trigger))
                yield return item;
        }

        if (scope is not null && !scopeMatchedAnySource)
            yield return new EnumerationFault(
                $"scope path \"{scopeRoot}\" is not under any Source of the profile", EnumerationSeverity.Fatal);
    }

    /// <summary>Depth convention: a file directly in the source root has depth 0 (its relative
    /// path contains no separators); contents of a directory whose relative path has k
    /// separators sit at depth k+1. MaxDepth prunes descent, not just matching.</summary>
    private IEnumerable<Result<Payload, EnumerationFault>> Walk(
        Guid profileId, string sourceRoot, string walkRoot, int? maxDepth, TriggerKind trigger)
    {
        Stack<string> pending = new();
        pending.Push(walkRoot);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            bool isWalkRoot = string.Equals(directory, walkRoot, StringComparison.OrdinalIgnoreCase);

            foreach (var entry in fileSystem.EnumerateEntries(directory))
            {
                if (entry.TryGetError(out EnumerationFault fault))
                {
                    if (fault.Severity == EnumerationSeverity.Fatal && !isWalkRoot)
                    {
                        // A subdirectory that cannot be opened must not kill the whole scan —
                        // downgrade to Warning and continue with siblings. Only the walk root's
                        // own failure is genuinely fatal (nothing was scanned at all).
                        yield return new EnumerationFault(
                            $"subdirectory skipped: {fault.Message}", EnumerationSeverity.Warning);
                        break;   // Fatal is the enumerator's terminal item for this directory
                    }
                    yield return fault;
                    if (fault.Severity == EnumerationSeverity.Fatal)
                        yield break;
                    continue;
                }

                entry.TryGetValue(out FileSystemEntry? item);
                if (item!.IsDirectory)
                {
                    if (IsInfrastructureDirectoryName(item.FileName))
                    {
                        logger.LogDebug("Skipping infrastructure directory {Path}", item.FullPath);
                        continue;
                    }
                    int contentsDepth = RelativeDepth(sourceRoot, item.FullPath) + 1;
                    if (maxDepth is int limit && contentsDepth > limit)
                        continue;
                    pending.Push(item.FullPath);
                }
                else
                {
                    if (item.FileName.Contains(TempFileMarker, StringComparison.OrdinalIgnoreCase))
                        continue;
                    yield return new Payload(profileId, item.FullPath, sourceRoot, trigger, time.GetUtcNow(),
                        MetadataFrom(item));
                }
            }
        }
    }

    /// <summary>Builds the stat snapshot from the enumeration entry so callers avoid a second
    /// stat. Mirrors <see cref="FileMetadataReader"/>'s attribute/timestamp derivation exactly
    /// (UTC timestamps; Hidden/System/ReparsePoint from the attribute flags).</summary>
    private static FileMetadata MetadataFrom(FileSystemEntry item) => new()
    {
        Length = item.Size,
        LastWritten = item.Modified.ToUniversalTime(),
        Created = item.Created,
        IsHidden = (item.Attributes & FileAttributes.Hidden) != 0,
        IsSystem = (item.Attributes & FileAttributes.System) != 0,
        IsSymlink = (item.Attributes & FileAttributes.ReparsePoint) != 0,
    };

    private static int RelativeDepth(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        int depth = 0;
        foreach (char c in relative)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                depth++;
        }
        return depth;
    }

    private static bool IsInfrastructurePath(string path)
    {
        if (Path.GetFileName(path).Contains(TempFileMarker, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (string segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (IsInfrastructureDirectoryName(segment))
                return true;
        }
        return false;
    }

    // Manual scan over the fixed two-element set — avoids the per-entry enumerator that
    // Enumerable.Contains(comparer) allocates on the scanner's hot path.
    private static bool IsInfrastructureDirectoryName(string name)
    {
        foreach (string infra in InfrastructureDirectories)
        {
            if (string.Equals(name, infra, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
