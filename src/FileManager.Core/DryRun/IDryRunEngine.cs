using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

public interface IDryRunEngine
{
    /// <summary>Previews the given profile object directly (a persisted profile the handler resolved
    /// from the catalog, or an unsaved in-memory draft). Read-only (I-DRYRUN-RO) like every simulate
    /// path — catalog membership was never what enforced that.</summary>
    Task<Result<DryRunReport, string>> SimulateAsync(
        Profile profile, string? scopePath, CancellationToken ct = default);

    /// <summary>Streams the report as chunks (no single-frame size ceiling). Each chunk carries a
    /// slice of the four report collections with <b>globally-assigned</b> indices — the consumer
    /// appends chunks in receive order so an op's SourceIndex/SubjectIndex stays a valid position
    /// into the fully assembled lists. A fatal setup/scan error is a single failure item that ends
    /// the stream; cancellation surfaces as <see cref="OperationCanceledException"/> from the
    /// enumerator. The stream carries only the per-source-file phase; the destination sweep
    /// (orphans / untouched) is run by the handler after the file phase. When
    /// <paramref name="progress"/> is supplied, its source counter is incremented per scanned
    /// candidate so a caller can sample it for live progress.</summary>
    IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
        Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
        CancellationToken ct = default);
}

/// <summary>One streamed slice of a dry-run report. Indices in the operations are already global
/// (positions into the fully assembled report lists), so the client simply concatenates chunks in
/// receive order. <paramref name="ScanTruncated"/> is set once the source scan hit its candidate
/// safety bound — the handler must OR it into its own truncation flag before running the destination
/// sweep, since a truncated (prefix-only) survivor set would make every orphan judgement unsound.
/// <para><b>Pool-ownership invariant (I-POOL-RECYCLE).</b> The element type is the read-only
/// <see cref="IPhysicalFileView"/>/<see cref="IFileOperationView"/> view, not the concrete record,
/// because a spilled streamed run backs a chunk with <em>pool-owned mutable carriers</em>
/// (<c>DryRunEngine</c>'s spool read path). A streamed chunk's entries are valid <b>only until the
/// consumer requests the next chunk</b>: the engine recycles that chunk's carriers back to its per-run
/// pool the instant the <c>yield return</c> resumes (safe because the IPC server serializes each frame
/// synchronously before pulling the next). No consumer may retain a chunk's files/ops — or an object
/// reachable from them — past its own iteration step. Copy out anything you keep (paths, sizes),
/// exactly as the existing consumers do.</para></summary>
/// <para><paramref name="SweepCapped"/> is the destination sweep's counterpart, set on a trailing
/// marker chunk when <c>DestinationProjector.SweepStreamAsync</c> hit its entry bound. It needs its own
/// flag because <paramref name="ScanTruncated"/> is ORed in by the handler BEFORE the sweep runs (it is
/// what suppresses the sweep entirely), so it cannot carry a signal the sweep discovers afterwards.</para>
public sealed record DryRunChunk(
    IReadOnlyList<IPhysicalFileView> SourceFiles,
    IReadOnlyList<IPhysicalFileView> DestinationFiles,
    IReadOnlyList<IFileOperationView> SourceOperations,
    IReadOnlyList<IFileOperationView> DestinationOperations,
    bool ScanTruncated = false,
    bool SweepCapped = false);

/// <summary>The BATCHED destination sweep's output (<c>DestinationProjector.Sweep</c>/<c>Project</c>).
/// No longer the streamed path's currency — <c>SweepStreamAsync</c> emits <see cref="DryRunChunk"/>s
/// directly, precisely so it never has to materialize these two full lists.
/// <para>Pre-existing files under the target roots that no source
/// writes to, each paired with its operation. <see cref="Ops"/>[i] references <see cref="Files"/>[i]
/// (its <c>SubjectIndex</c> is <c>i</c>); a caller merging this into a larger report offsets those
/// indices by the count of destination files already collected. <see cref="WalkMs"/>/<see cref="MergeMs"/>
/// split the sweep's own wall time (parallel directory walk vs. the serial sort merge) for the
/// timing audit; both are 0 on the early-return (truncated / no target) paths.</para></summary>
public readonly record struct DestinationSweepResult(
    IReadOnlyList<PhysicalFile> Files,
    IReadOnlyList<VirtualFileOperation> Ops,
    bool Truncated = false,
    long WalkMs = 0,
    long MergeMs = 0);
