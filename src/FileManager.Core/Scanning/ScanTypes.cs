using System;
using System.Collections.Generic;
using System.Threading;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;

namespace FileManager.Core.Scanning;

/// <summary>A directory awaiting enumeration, tagged with the volume it lives on (so the scheduler can
/// enforce the per-drive cap) and an opaque <see cref="Tag"/> the adapter uses to carry its own
/// per-directory context (source root, depth, is-root, target root, …). Children discovered under this
/// directory inherit its <see cref="VolumeKey"/>/<see cref="DriveClass"/>.</summary>
public readonly record struct ScanWorkItem(string Directory, string VolumeKey, DriveClass DriveClass, object? Tag);

/// <summary>One streamed result of a scan: either a file <see cref="Entry"/> the adapter asked to emit,
/// or an <see cref="EnumerationFault"/>. Exactly one of the two is set. <see cref="Tag"/> is the tag of
/// the directory the result came from.</summary>
public readonly record struct ScanResult(FileSystemEntry? Entry, EnumerationFault? Fault, object? Tag);

/// <summary>An adapter's decision about a discovered subdirectory: whether to descend into it, and the
/// tag to stamp on its work item (e.g. propagating the source root while marking it non-root).</summary>
public readonly record struct ChildDecision(bool Descend, object? ChildTag);

/// <summary>The policy an adapter supplies for one scan session. The scheduler owns traversal
/// mechanics (enumerate, push children, stream results, honor caps/cancellation); these callbacks own
/// all policy (filtering, depth pruning, fault mapping). Callbacks run on scheduler worker threads and
/// must be thread-safe if the session runs at more than <see cref="MaxConcurrency"/> 1.</summary>
public sealed record ScanSessionOptions
{
    /// <summary>Upper bound on how many scan workers this session may occupy at once. Default unbounded
    /// (the global and per-drive caps bind). A test pins this to 1 for deterministic, serial walks.</summary>
    public int MaxConcurrency { get; init; } = int.MaxValue;

    /// <summary>Bound on results buffered ahead of the consumer. A worker that fills it parks the
    /// current directory and moves to other work rather than blocking, so a slow consumer on one
    /// session never stalls workers serving another.</summary>
    public int OutputCapacity { get; init; } = 4096;

    /// <summary>Decides whether to descend into a discovered subdirectory and what tag its work item
    /// carries. Never emits a result — directories are structural.</summary>
    public required Func<FileSystemEntry, object?, ChildDecision> OnSubdirectory { get; init; }

    /// <summary>Returns whether a discovered file should be emitted as a <see cref="ScanResult"/>. Any
    /// per-file side effect (e.g. a candidate-cap reservation) runs exactly once here.</summary>
    public required Func<FileSystemEntry, object?, bool> OnFile { get; init; }

    /// <summary>Maps an enumeration fault to the fault to emit (e.g. downgrading a subdirectory's Fatal
    /// to a Warning), or null to suppress it. Absent (null) forwards faults unchanged. The scheduler
    /// treats an original-Fatal fault as terminal for its directory regardless of the mapping.</summary>
    public Func<EnumerationFault, object?, EnumerationFault?>? OnFault { get; init; }
}

/// <summary>The process-wide scan scheduler: a single long-lived owner of all directory-enumeration
/// worker threads, enforcing a global thread ceiling and a per-drive cap shared across every session.</summary>
public interface IScanScheduler
{
    /// <summary>Opens a session. Submit root work items, then drain <see cref="IScanSession.Consume"/>.
    /// Dispose (or an early break out of Consume) tears the session down without disturbing others.</summary>
    IScanSession OpenSession(ScanSessionOptions options, CancellationToken ct);
}

/// <summary>A single logical scan. Its work shares the scheduler's global worker pool and per-drive
/// budgets with every other open session, but its results, completion, and cancellation are isolated.</summary>
public interface IScanSession : IDisposable
{
    /// <summary>Queues a directory for enumeration. Safe to call before and during consumption (workers
    /// call it to push discovered subdirectories).</summary>
    void Submit(ScanWorkItem item);

    /// <summary>Lazily yields this session's results until it completes naturally or is cancelled.
    /// Blocks the calling thread while awaiting more. Breaking out early tears the session down.</summary>
    IEnumerable<ScanResult> Consume();
}
