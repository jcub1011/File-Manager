using FileManager.Contracts.Profiles;
using FileManager.Contracts.Primitives;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Watching;

/// <summary>The single enumeration path turning a profile (or one scoped root/file) into
/// candidate Payloads (§4.2): a work-stealing DFS over IFileSystemService, honoring the merged
/// MaxDepth and the unconditional infrastructure exclusions (I-INFRA-EXCLUDED).
///
/// Enumeration is I/O-bound (each directory read blocks), so — like the dry-run destination sweep
/// (<see cref="DryRun.DestinationProjector"/>) — every source root is pushed onto one shared queue
/// drained by dedicated (LongRunning) threads, overlapping the blocking reads rather than issuing
/// them one at a time, and off the thread pool so a high worker count never starves it. The degree
/// of parallelism follows the source medium (local volumes are CPU/kernel-bound; network shares are
/// latency-bound and want heavy oversubscription) unless a Manual worker count is pinned. The
/// producers feed a bounded <see cref="BlockingCollection{T}"/> that the returned lazy sequence
/// drains, so the streaming contract is preserved and a huge tree can't balloon memory ahead of the
/// consumer. Emission order is non-deterministic, which is why every consumer sorts by source path
/// before use.</summary>
public sealed class SourceScanner(
    ILogger<SourceScanner> logger, IFileSystemService fileSystem, TimeProvider time,
    IVolumeInfoProvider? volumes = null) : ISourceScanner
{
    /// <summary>Backpressure bound on the producer→consumer buffer: enough that producers rarely
    /// block on a keeping-up consumer, small enough that a pathological tree can't buffer unbounded
    /// payloads ahead of it.</summary>
    private const int OutputBufferCapacity = 4096;

    public IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null,
        int? manualWorkers = null, CancellationToken ct = default)
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

        foreach (Result<Payload, EnumerationFault> result in WalkParallel(profile.Id, trigger, seeds, manualWorkers, ct))
            yield return result;
    }

    /// <summary>Fans the seeds out across <paramref name="manualWorkers"/> (or an auto-scaled) dedicated
    /// threads that drain a shared directory queue, and streams their payloads/faults through a bounded
    /// buffer to the caller. The <c>finally</c> tears the producers down on early break (the batched
    /// engine stops at its file cap) as well as on natural completion.</summary>
    private IEnumerable<Result<Payload, EnumerationFault>> WalkParallel(
        Guid profileId, TriggerKind trigger, List<Seed> seeds, int? manualWorkers, CancellationToken ct)
    {
        int dop = Math.Max(1, manualWorkers ?? AutoScanWorkers(seeds));

        // A worker blocked on a bounded Add is unblocked by cancelling this token (the Add throws,
        // caught in Drain) — the mechanism that stops producers when the consumer breaks early.
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ScanState state = new(OutputBufferCapacity);
        foreach (Seed seed in seeds)
            state.Enqueue(new WorkItem(seed.WalkRoot, seed.SourceRoot, seed.MaxDepth, IsRoot: true));

        // Each thread drains the shared queue: enumerate a directory (the blocking I/O), emit its
        // files, push its subdirectories back. Termination mirrors the sweep: exit when the stack is
        // empty AND no peer can still push (outstanding == 0), or when torn down.
        void Drain()
        {
            SpinWait spin = default;
            try
            {
                while (!linkedCts.IsCancellationRequested)
                {
                    if (state.TryTake(out WorkItem item))
                    {
                        try
                        {
                            WalkDir(item, state, profileId, trigger, linkedCts.Token);
                        }
                        finally
                        {
                            state.Done();   // must run even on an unexpected throw, or peers spin forever
                        }
                        spin = default;      // found work — reset the idle backoff
                        continue;
                    }

                    if (state.AllDrained)
                        break;
                    spin.SpinOnce();   // empty for now but a peer may still push; back off (spins → sleeps)
                }
            }
            catch (OperationCanceledException)
            {
                // Early break/cancel: a blocked Add threw once the consumer stopped draining. Unwind.
            }
        }

        Task[] threads = new Task[dop];
        for (int i = 0; i < dop; i++)
            threads[i] = Task.Factory.StartNew(
                Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Complete the buffer once every worker has exited, so the consumer's enumeration terminates.
        Task completion = Task.Factory.StartNew(
            () => { try { Task.WaitAll(threads); } finally { state.Output.CompleteAdding(); } },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            // Passing the caller's token makes a cancelled scan throw OperationCanceledException from
            // the enumerator (standard IEnumerable cancellation semantics) even when no payload was
            // produced yet — rather than silently completing empty.
            foreach (Result<Payload, EnumerationFault> result in state.Output.GetConsumingEnumerable(ct))
                yield return result;

            // A cancellation that raced the producers to CompleteAdding leaves the loop above to
            // finish empty without observing the token (BlockingCollection stops at IsCompleted first).
            // Surface it deterministically so cancellation never resolves as a normal empty scan.
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // Runs on natural completion AND on early break/dispose. Cancel so any worker blocked on a
            // full buffer unblocks, then wait for a clean teardown before disposing the buffer.
            linkedCts.Cancel();
            try { completion.Wait(); }
            catch (Exception ex) { logger.LogDebug(ex, "Source scan producers faulted during teardown"); }
            state.Output.Dispose();
        }
    }

    /// <summary>Enumerates one directory (single level): pushes descendable subdirectories back onto
    /// the queue and emits each classifiable file. Fault handling matches the former serial walk —
    /// a subdirectory fault is downgraded to a Warning so siblings continue, while the walk root's
    /// own failure stays Fatal (the consumer treats any Fatal as terminal). Depth convention: a file
    /// directly in the source root has depth 0; contents of a directory whose relative path has k
    /// separators sit at depth k+1. MaxDepth prunes descent, not just matching.</summary>
    private void WalkDir(WorkItem item, ScanState state, Guid profileId, TriggerKind trigger, CancellationToken token)
    {
        foreach (var entry in fileSystem.EnumerateEntries(item.Dir))
        {
            if (entry.TryGetError(out EnumerationFault fault))
            {
                if (fault.Severity == EnumerationSeverity.Fatal && !item.IsRoot)
                {
                    // A subdirectory that cannot be opened must not kill the whole scan —
                    // downgrade to Warning and continue with siblings.
                    state.Output.Add(
                        new EnumerationFault($"subdirectory skipped: {fault.Message}", EnumerationSeverity.Warning), token);
                    break;   // Fatal is the enumerator's terminal item for this directory
                }
                state.Output.Add(fault, token);
                if (fault.Severity == EnumerationSeverity.Fatal)
                    break;   // walk-root failure: terminal for this subtree (its children were never queued)
                continue;
            }

            entry.TryGetValue(out FileSystemEntry? fsItem);
            if (fsItem!.IsDirectory)
            {
                if (InfrastructurePaths.IsInfrastructureDirectoryName(fsItem.FileName))
                {
                    logger.LogDebug("Skipping infrastructure directory {Path}", fsItem.FullPath);
                    continue;
                }
                int contentsDepth = RelativeDepth(item.SourceRoot, fsItem.FullPath) + 1;
                if (item.MaxDepth is int limit && contentsDepth > limit)
                    continue;
                state.Enqueue(new WorkItem(fsItem.FullPath, item.SourceRoot, item.MaxDepth, IsRoot: false));
            }
            else
            {
                if (InfrastructurePaths.IsTempFileName(fsItem.FileName))
                    continue;
                state.Output.Add(
                    new Payload(profileId, fsItem.FullPath, item.SourceRoot, trigger, time.GetUtcNow(), MetadataFrom(fsItem)),
                    token);
            }
        }
    }

    /// <summary>Degree of parallelism for the walk in Automatic mode. Enumeration blocks, so local
    /// volumes (CPU/kernel-bound) saturate at roughly the core count, while network shares
    /// (latency-bound) benefit from heavy oversubscription to overlap the round-trips. A single
    /// shared pool drains all source roots, so size to the most-latent source. When no volume
    /// provider was supplied (unit/benchmark construction over local temp trees), assume local.</summary>
    private int AutoScanWorkers(List<Seed> seeds)
    {
        if (volumes is not null)
            foreach (Seed seed in seeds)
                if (volumes.IsNetworkPath(seed.SourceRoot))
                    return Math.Clamp(Environment.ProcessorCount * 4, 16, 64);
        return Math.Max(1, Environment.ProcessorCount);
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

    /// <summary>A directory awaiting enumeration, tagged with its source root (for
    /// <see cref="Payload.SourceRoot"/> + depth) and the merged MaxDepth. <see cref="IsRoot"/>
    /// distinguishes a seed walk root (whose enumeration failure is Fatal) from a descended
    /// subdirectory (whose failure is downgraded to a Warning).</summary>
    private readonly record struct WorkItem(string Dir, string SourceRoot, int? MaxDepth, bool IsRoot);

    /// <summary>Shared state for the work-stealing walk: the directory queue, the outstanding-work
    /// counter that drives termination, and the bounded output buffer — all mutated concurrently, so
    /// every mutation is interlocked (mirrors the sweep's SweepState).</summary>
    private sealed class ScanState
    {
        public readonly BlockingCollection<Result<Payload, EnumerationFault>> Output;
        private readonly ConcurrentStack<WorkItem> _pending = new();
        // Directories queued OR being processed. Incremented on Enqueue, decremented on Done; a
        // worker exits only when it sees an empty stack AND this at zero (no peer can still push).
        private long _outstanding;

        public ScanState(int capacity) =>
            Output = new BlockingCollection<Result<Payload, EnumerationFault>>(capacity);

        public bool AllDrained => Interlocked.Read(ref _outstanding) == 0;

        public void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref _outstanding);
            _pending.Push(item);
        }

        public bool TryTake(out WorkItem item) => _pending.TryPop(out item);

        public void Done() => Interlocked.Decrement(ref _outstanding);
    }
}
