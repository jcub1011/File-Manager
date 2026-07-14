using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Contracts.Primitives;
using System.Collections.Generic;
using System.Threading;

namespace FileManager.Core.Watching;

public interface ISourceScanner
{
    /// <param name="manualWorkers">A pinned walk worker count (Manual concurrency), or null to
    /// auto-scale the degree of parallelism to the source medium (local vs. network).</param>
    /// <param name="ct">Cancels the walk; breaking out of the returned sequence early also tears the
    /// producer threads down.</param>
    IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null,
        int? manualWorkers = null, CancellationToken ct = default);
}
