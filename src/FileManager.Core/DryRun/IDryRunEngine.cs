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
/// sweep, since a truncated (prefix-only) survivor set would make every orphan judgement unsound.</summary>
public sealed record DryRunChunk(
    IReadOnlyList<PhysicalFile> SourceFiles,
    IReadOnlyList<PhysicalFile> DestinationFiles,
    IReadOnlyList<VirtualFileOperation> SourceOperations,
    IReadOnlyList<VirtualFileOperation> DestinationOperations,
    bool ScanTruncated = false);

/// <summary>The destination sweep's output: pre-existing files under the target roots that no source
/// writes to, each paired with its operation. <see cref="Ops"/>[i] references <see cref="Files"/>[i]
/// (its <c>SubjectIndex</c> is <c>i</c>); a caller merging this into a larger report offsets those
/// indices by the count of destination files already collected. <see cref="WalkMs"/>/<see cref="MergeMs"/>
/// split the sweep's own wall time (parallel directory walk vs. the serial sort/dedup merge) for the
/// timing audit; both are 0 on the early-return (truncated / no target) paths.</summary>
public readonly record struct DestinationSweepResult(
    IReadOnlyList<PhysicalFile> Files,
    IReadOnlyList<VirtualFileOperation> Ops,
    bool Truncated = false,
    long WalkMs = 0,
    long MergeMs = 0);
