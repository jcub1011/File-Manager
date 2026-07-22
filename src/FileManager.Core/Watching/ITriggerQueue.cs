using FileManager.Core.Jobs;
using System.Collections.Generic;
using System.Threading;

namespace FileManager.Core.Watching;

public interface ITriggerQueue
{
    void Enqueue(Payload payload);
    IAsyncEnumerable<Payload> DequeueAsync(CancellationToken ct = default);
    int PendingCount { get; }
}
