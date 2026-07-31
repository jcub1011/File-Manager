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
    private long _skipped;

    public void SourceDiscovered() => Interlocked.Increment(ref _sources);
    public void DestinationDiscovered() => Interlocked.Increment(ref _destinations);

    /// <summary>One entry the walk could not read — an ACL-denied or locked subdirectory, downgraded
    /// from Fatal to Warning by <c>SourceScanner.OnFault</c> so its siblings are still walked. Unlike
    /// the other two this is not a progress figure: it is the count that tells the caller its report
    /// covers a PARTIAL tree, which the report itself has no field for.</summary>
    public void EntrySkipped() => Interlocked.Increment(ref _skipped);

    public long Sources => Interlocked.Read(ref _sources);
    public long Destinations => Interlocked.Read(ref _destinations);
    public long Skipped => Interlocked.Read(ref _skipped);
}
