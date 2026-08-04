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
/// DIFFERENT list from the one the run will execute, which would defeat the entire purpose.</para></summary>
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

        // Source side: one file + one Processed operation per planned copy.
        List<PhysicalFile> sourceFiles = [];
        List<VirtualFileOperation> sourceOps = [];
        int sourceIndex = 0;
        foreach (RunCopyItem item in RunSnapshotReader.ReadCopies(directory, logger))
        {
            ct.ThrowIfCancellationRequested();
            sourceFiles.Add(new PhysicalFile
            {
                Path = item.SourcePath,
                Root = item.SourceRoot,
                Length = item.SizeBytes,
                LastWritten = item.LastWriteUtc,
            });
            sourceOps.Add(new VirtualFileOperation
            {
                Path = item.SourcePath,
                Root = item.SourceRoot,
                Kind = item.PlannedKind,
                SourceIndex = sourceIndex,
                SourceDisposition = header!.Profile.Policies.OnSuccess,
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

        // Destination side: one file + one Deleted operation per planned orphan. These are the rows
        // that matter most — they are the destructive half the user is being asked to approve.
        List<PhysicalFile> destFiles = [];
        List<VirtualFileOperation> destOps = [];
        int destIndex = 0;
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
