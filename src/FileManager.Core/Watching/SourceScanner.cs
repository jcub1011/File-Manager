using FileManager.Contracts.Profiles;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using FileManager.Core.Scanning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FileManager.Core.Watching;

/// <summary>The single enumeration path turning a profile (or one scoped root/file) into candidate
/// Payloads (§4.2). It is a thin adapter over the process-wide <see cref="IScanScheduler"/>: a serial
/// pre-pass resolves scope and collects one walk seed per source, then the seeds are submitted to a
/// scan session and the session's streamed results are mapped to Payloads/faults.
///
/// The scheduler owns the traversal mechanics and the global/per-drive thread budgets; this adapter
/// supplies only policy via the session callbacks — the merged MaxDepth, the unconditional
/// infrastructure exclusions (I-INFRA-EXCLUDED), and the root-vs-subdirectory fault severity rule.
/// Emission order is non-deterministic (the walk is concurrent), which is why every consumer sorts by
/// source path before use.</summary>
public sealed class SourceScanner(
    TimeProvider time, IScanScheduler scheduler, IVolumeInfoProvider? volumes = null) : ISourceScanner
{
    /// <summary>Backpressure bound on the session's output buffer: enough that workers rarely park on a
    /// keeping-up consumer, small enough that a pathological tree can't buffer unbounded payloads.</summary>
    private const int OutputBufferCapacity = 4096;

    public IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null, CancellationToken ct = default)
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

        // Serial pre-pass (cheap): resolve scope, emit the single-file-scope payload inline, and
        // collect one walk seed per source. The parallel walk runs once, over all seeds, afterward.
        List<Seed> seeds = [];
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
                    if (!InfrastructurePaths.IsInfrastructurePath(scoped.Value))
                        yield return new Payload(profile.Id, scoped.Value, sourceRoot.Value, trigger, time.GetUtcNow());
                    continue;
                }
                walkRoot = scoped.Value;
            }

            int? maxDepth = source.Filters?.MaxDepth ?? profile.Filters?.MaxDepth;
            seeds.Add(new Seed(walkRoot, sourceRoot.Value, maxDepth));
        }

        if (scope is not null && !scopeMatchedAnySource)
            yield return new EnumerationFault(
                $"scope path \"{scopeRoot}\" is not under any Source of the profile", EnumerationSeverity.Fatal);

        if (seeds.Count == 0)
            yield break;   // nothing to walk (file scope handled inline, or no source matched)

        foreach (Result<Payload, EnumerationFault> result in WalkScheduled(profile.Id, trigger, seeds, ct))
            yield return result;
    }

    /// <summary>Opens a scan session, submits one work item per seed, and maps the streamed results to
    /// Payloads/faults. The session is disposed on natural completion AND on early break (the batched
    /// engine stops at its file cap), tearing this session's work down without disturbing others.</summary>
    private IEnumerable<Result<Payload, EnumerationFault>> WalkScheduled(
        Guid profileId, TriggerKind trigger, List<Seed> seeds, CancellationToken ct)
    {
        ScanSessionOptions options = new()
        {
            OutputCapacity = OutputBufferCapacity,
            // Descend into a subdirectory unless it is an infrastructure dir or beyond MaxDepth. A file
            // directly in the source root is depth 0; contents of a directory whose relative path has k
            // separators sit at depth k+1. MaxDepth prunes descent, not just matching.
            OnSubdirectory = static (entry, tag) =>
            {
                SourceTag t = (SourceTag)tag!;
                if (InfrastructurePaths.IsInfrastructureDirectoryName(entry.FileName))
                    return new ChildDecision(false, null);
                int contentsDepth = RelativeDepth(t.SourceRoot, entry.FullPath) + 1;
                if (t.MaxDepth is int limit && contentsDepth > limit)
                    return new ChildDecision(false, null);
                return new ChildDecision(true, new SourceTag(t.SourceRoot, t.MaxDepth, IsRoot: false));
            },
            OnFile = static (entry, _) => !InfrastructurePaths.IsTempFileName(entry.FileName),
            // A subdirectory that cannot be opened must not kill the whole scan — downgrade its Fatal to
            // a Warning so siblings continue; the walk root's own failure stays Fatal (terminal).
            OnFault = static (fault, tag) =>
            {
                SourceTag t = (SourceTag)tag!;
                if (fault.Severity == EnumerationSeverity.Fatal && !t.IsRoot)
                    return new EnumerationFault($"subdirectory skipped: {fault.Message}", EnumerationSeverity.Warning);
                return fault;
            },
        };

        using IScanSession session = scheduler.OpenSession(options, ct);
        foreach (Seed seed in seeds)
        {
            (string key, DriveClass driveClass) = ResolveVolume(seed.WalkRoot);
            session.Submit(new ScanWorkItem(seed.WalkRoot, key, driveClass, new SourceTag(seed.SourceRoot, seed.MaxDepth, IsRoot: true)));
        }

        foreach (ScanResult result in session.Consume())
        {
            if (result.Fault is EnumerationFault fault)
                yield return fault;
            else if (result.Entry is FileSystemEntry entry)
                yield return new Payload(
                    profileId, entry.FullPath, ((SourceTag)result.Tag!).SourceRoot, trigger, time.GetUtcNow(), MetadataFrom(entry));
        }

        // A cancelled scan must surface OperationCanceledException from the enumerator (standard
        // IEnumerable cancellation semantics) rather than resolving as a normal empty scan.
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The volume key + drive class for a walk root. When no volume provider was supplied
    /// (unit/benchmark construction over local temp trees) everything is one synthetic local volume.</summary>
    private (string Key, DriveClass DriveClass) ResolveVolume(string path)
    {
        if (volumes is null)
            return ("local", DriveClass.Fixed);
        string key = volumes.GetVolumeKey(path).TryGetValue(out string? resolved) ? resolved : "local";
        return (key, volumes.GetDriveClass(path));
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

    /// <summary>One source's walk parameters, resolved in the serial pre-pass: the directory to walk
    /// (the source root, or a narrower directory scope), the source root to stamp on payloads and
    /// measure depth against, and the merged MaxDepth.</summary>
    private readonly record struct Seed(string WalkRoot, string SourceRoot, int? MaxDepth);

    /// <summary>The per-directory scan-session tag: the source root (for <see cref="Payload.SourceRoot"/>
    /// and depth), the merged MaxDepth, and whether this is a seed walk root (its enumeration failure is
    /// Fatal) versus a descended subdirectory (whose failure is downgraded to a Warning).</summary>
    private sealed record SourceTag(string SourceRoot, int? MaxDepth, bool IsRoot);
}
