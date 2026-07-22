using System.Threading;

namespace FileManager.Core.DryRun;

/// <summary>Live discovery counters for one streamed dry run, shared between the producers (the
/// engine's scan pump, the destination sweep's walkers) and a reader that samples them on a timer
/// to emit throttled progress frames. Increments are lock-free; reads are point-in-time snapshots —
/// the assembled report, not these counters, is the source of truth for final totals.</summary>
public sealed class DryRunProgressCounters
{
    private long _sources;
    private long _destinations;

    public void SourceDiscovered() => Interlocked.Increment(ref _sources);
    public void DestinationDiscovered() => Interlocked.Increment(ref _destinations);

    public long Sources => Interlocked.Read(ref _sources);
    public long Destinations => Interlocked.Read(ref _destinations);
}
