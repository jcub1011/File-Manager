using FileManager.Core.Jobs;
using System;

namespace FileManager.Core.Watching;

public interface ISettleTracker
{
    /// <summary>
    /// Begins watching the file change event.
    /// </summary>
    /// <param name="change"></param>
    void Observe(FileChangeEvent change);

    /// <summary>
    /// Subscribes to a file ready event. Unsubscribes on disposal.
    /// </summary>
    /// <param name="readyHandler"></param>
    /// <returns></returns>
    IDisposable SubscribeToReady(Action<Payload> readyHandler);
}
