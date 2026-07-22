using FileManager.Contracts.Primitives;
using System;

namespace FileManager.Core.Watching;

public interface IWatcherService
{
    /// <summary>
    /// Begins watching a root.
    /// </summary>
    /// <returns></returns>
    Result Start();

    /// <summary>
    /// Stops watching a root.
    /// </summary>
    void Stop();

    /// <summary>
    /// Subscribes the changeHandler to file change events. Unsubscribes on disposal.
    /// </summary>
    /// <returns></returns>
    IDisposable Subscribe(Action<FileChangeEvent> changeHandler);

    /// <summary>Subscribes to watch interruptions: the OS event buffer overflowed
    /// (InternalBufferOverflowException), the watcher was restarted after an error, or the watched
    /// root disappeared/reappeared (drive removal). Per-file events were LOST across the gap, so
    /// the consumer must rescan the affected root rather than trust the change stream — without
    /// this seam an implementation has no way to say so, and a future FileSystemWatcher backend
    /// would be forced to drop the overflow silently. Unsubscribes on disposal.</summary>
    IDisposable SubscribeInterruptions(Action<WatchInterruptedEvent> interruptionHandler);
}

public readonly record struct FileChangeEvent(
    Guid ProfileId, string SourceRoot, string FullPath, DateTimeOffset ObservedAt, FileChangeKind Kind = FileChangeKind.CreatedOrChanged);

/// <summary>What the OS reported for the path — Deleted/Renamed matter to settle-tracking and to a
/// future Mirror trigger, and a change stream without kinds cannot distinguish "new work" from
/// "work that vanished".</summary>
public enum FileChangeKind
{
    CreatedOrChanged,
    Deleted,
    RenamedFrom,
    RenamedTo,
}

/// <summary>A gap in the change stream for one source root: events between the interruption and the
/// consumer's rescan may have been lost. <see cref="Reason"/> is human-readable diagnostics; the
/// contract is simply "rescan <see cref="SourceRoot"/>".</summary>
public readonly record struct WatchInterruptedEvent(
    Guid ProfileId, string SourceRoot, string Reason, DateTimeOffset ObservedAt);
