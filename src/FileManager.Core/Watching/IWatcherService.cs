using FileManager.Core.Primitives;
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
}

public readonly record struct FileChangeEvent(
    Guid ProfileId, string SourceRoot, string FullPath, DateTimeOffset ObservedAt);
