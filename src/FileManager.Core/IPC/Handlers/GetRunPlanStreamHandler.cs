using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.DryRun;
using FileManager.Core.Runs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Replays a pending run's frozen work list as the same <c>DryRunChunkResponse</c> frames a
/// preview streams, terminated by a <c>DryRunCompleteResponse</c>.
///
/// <para><b>Why the same wire shape.</b> The approval view IS the dry-run view. Giving the run's plan
/// its own frame type would mean a second renderer for the same information, and the whole point of
/// showing the user their work list before it runs is that they see it through the code path they
/// already read previews with. So this reads the snapshot back and re-emits it in the preview's
/// currency — no new client rendering at all.</para>
///
/// <para>Rows are reconstructed from the snapshot rather than re-planned: re-scanning would produce a
/// DIFFERENT list from the one the run will execute, which would defeat the entire purpose.</para>
///
/// <para><b>This reads the snapshot's DISPLAY halves, never its executable ones.</b> The two are different
/// sets, and using the wrong one produces an empty panel that reads as "the preview found nothing":
/// <c>copies.ndjsonl</c> omits every filtered and already-up-to-date file, so an already-synchronized
/// profile has no copies at all, and <c>deletes.ndjsonl</c> holds only orphans, so a profile that removes
/// nothing has no destination rows. <c>sources.ndjsonl</c> and <c>destinations.ndjsonl</c> are the full
/// picture the plan actually formed, which is what the user is being asked to approve.</para></summary>
public sealed class GetRunPlanStreamHandler(
    IRunCoordinator runs, ILogger<GetRunPlanStreamHandler> logger) : IIpcStreamingRequestHandler
{
    /// <summary>How many rows go in one frame. The wire budget is a byte figure, but a snapshot row is
    /// a bounded, known shape (two paths and a few scalars), so a row count is a simpler bound that
    /// lands frames comfortably inside the same envelope.</summary>
    private const int RowsPerChunk = 512;

    public string RequestType => IpcRequestTypes.GetRunPlanStream;

    /// <summary>Never invoked — the server routes streaming handlers to <see cref="HandleStreamAsync"/>.</summary>
    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(GetRunPlanStreamHandler)} is a streaming handler; the server must call {nameof(HandleStreamAsync)}");

    public async IAsyncEnumerable<IpcResponse> HandleStreamAsync(
        IpcRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var typed = (GetRunPlanStreamRequest)request;
        if (runs.SnapshotDirectory(typed.RunId) is not { } directory)
        {
            yield return new ErrorResponse { Code = "RUN_NOT_FOUND", Message = $"no run with id {typed.RunId}" };
            yield break;
        }

        Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(directory);
        if (read.TryGetError(out string? headerError))
        {
            yield return new ErrorResponse { Code = "RUN_PLAN_UNAVAILABLE", Message = headerError };
            yield break;
        }
        read.TryGetValue(out RunSnapshotHeader? header);

        // One converter for the whole stream, exactly as the preview handler does: it owns the
        // directory-index space across every frame, so each index stays a valid position into the
        // client's concatenated Directories list.
        DryRunStreamHandler.WireChunkConverter converter = new(recycleWireRecords: false);

        // Source side: one file + one operation per source the plan LOOKED at — sources.ndjsonl, not
        // copies.ndjsonl. The copy list holds only files there is work for, so reading it would drop every
        // filtered and every already-up-to-date file, and an already-synchronized profile would replay with
        // no source rows at all. It is also what makes the ordinals line up: a destination row names its
        // source by position in this list.
        List<PhysicalFile> sourceFiles = [];
        List<VirtualFileOperation> sourceOps = [];
        int sourceIndex = 0;
        foreach (RunSourceItem item in RunSnapshotReader.ReadSources(directory, logger))
        {
            ct.ThrowIfCancellationRequested();
            sourceFiles.Add(new PhysicalFile
            {
                Path = item.Path,
                Root = item.SourceRoot,
                Length = item.SizeBytes,
                LastWritten = item.LastWriteUtc,
            });
            sourceOps.Add(new VirtualFileOperation
            {
                Path = item.Path,
                Root = item.SourceRoot,
                Kind = item.Kind,
                SourceIndex = sourceIndex,
                // The recorded disposition, not the profile's setting: a skipped file has none, and
                // stamping one on it would count it toward the disposal total for work that will not happen.
                SourceDisposition = item.Disposition,
                Detail = item.Detail,
            });
            sourceIndex++;
            if (sourceFiles.Count >= RowsPerChunk)
            {
                yield return converter.Convert(sourceFiles, [], sourceOps, []);
                sourceFiles = [];
                sourceOps = [];
            }
        }
        if (sourceFiles.Count > 0)
            yield return converter.Convert(sourceFiles, [], sourceOps, []);

        // Destination side, in two passes over ONE global destination-file index space: the plan's
        // projection (where each copy lands, and what it lands on) and then the orphans. Both are
        // destination operations to a client, and the index must stay global across them because a
        // SubjectIndex is a position in the client's whole concatenated DestinationFiles list.
        List<PhysicalFile> destFiles = [];
        List<VirtualFileOperation> destOps = [];
        int destIndex = 0;

        // Pass 1: the projection. A row whose subject is null is a path that does not exist yet (New, or
        // a rename's new name), so it contributes an operation and NO file.
        foreach (RunDestinationItem item in RunSnapshotReader.ReadDestinations(directory, logger))
        {
            ct.ThrowIfCancellationRequested();
            int subject = -1;
            if (item.SubjectPath is { } subjectPath)
            {
                destFiles.Add(new PhysicalFile
                {
                    Path = subjectPath,
                    Root = item.TargetRoot,
                    Length = item.SubjectSizeBytes ?? 0,
                    LastWritten = item.SubjectLastWriteUtc ?? default,
                });
                subject = destIndex;
                destIndex++;
            }
            destOps.Add(new VirtualFileOperation
            {
                Path = item.Path,
                Root = item.TargetRoot,
                Kind = item.Kind,
                // The recorded ordinal IS the wire source index: source rows are emitted above in
                // sources.ndjsonl order, so an ordinal is a position in the client's source list.
                SourceIndex = item.SourceOrdinal,
                SubjectIndex = subject,
                Detail = item.Detail,
            });
            // Bounded on the OPERATION count, which is the row count here — a chunk of projection rows
            // may carry fewer files than operations.
            if (destOps.Count >= RowsPerChunk)
            {
                yield return converter.Convert([], destFiles, [], destOps);
                destFiles = [];
                destOps = [];
            }
        }
        if (destOps.Count > 0)
        {
            yield return converter.Convert([], destFiles, [], destOps);
            destFiles = [];
            destOps = [];
        }

        // Pass 2: one file + one Deleted operation per planned orphan. These are the rows that matter
        // most — they are the destructive half the user is being asked to approve.
        foreach (RunDeleteItem item in RunSnapshotReader.ReadDeletes(directory, logger))
        {
            ct.ThrowIfCancellationRequested();
            destFiles.Add(new PhysicalFile
            {
                Path = item.Path,
                Root = item.TargetRoot,
                Length = item.SizeBytes,
                LastWritten = item.LastWriteUtc,
            });
            destOps.Add(new VirtualFileOperation
            {
                Path = item.Path,
                Root = item.TargetRoot,
                Kind = OperationKind.Deleted,
                SubjectIndex = destIndex,
            });
            destIndex++;
            if (destFiles.Count >= RowsPerChunk)
            {
                yield return converter.Convert([], destFiles, [], destOps);
                destFiles = [];
                destOps = [];
            }
        }
        if (destFiles.Count > 0)
            yield return converter.Convert([], destFiles, [], destOps);

        yield return new DryRunCompleteResponse
        {
            GeneratedAt = header!.PlannedAtUtc,
            Truncated = header.Truncated,
            Space = header.Space,
        };
    }
}
