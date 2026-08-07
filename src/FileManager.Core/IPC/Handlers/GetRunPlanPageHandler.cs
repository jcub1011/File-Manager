using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.DryRun;
using FileManager.Core.Runs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Serves ONE WINDOW of a pending run's frozen work list, in the same
/// <c>DryRunChunkResponse</c> shape a whole-plan replay emits.
///
/// <para><b>Why the same wire shape, again.</b> <c>GetRunPlanStreamHandler</c> exists so the approval
/// view can be the dry-run view with no second renderer. A page is the same argument one level down: the
/// client folds it with the ingest it already has, so there is no second path that could disagree with
/// the first about what a row means.</para>
///
/// <para><b>Ordering and paging both come from the sidecars.</b> The order file turns a display position
/// into a row ordinal; the block index turns an ordinal into a byte offset. Both are optional — a
/// snapshot written before paging existed, or one whose sidecar write failed, simply has neither, and
/// this falls back to plan order and a sequential read. That fallback is slow on a huge plan, which is
/// the honest trade: the alternative is refusing to show a plan that is perfectly sound.</para>
///
/// <para>Unlike the stream this is a UNARY handler. A page is one frame by construction, so streaming it
/// would add a protocol phase to deliver a single response.</para></summary>
public sealed class GetRunPlanPageHandler(
    IRunCoordinator runs, ILogger<GetRunPlanPageHandler> logger) : IIpcRequestHandler
{
    /// <summary>Rows one request may return. A viewport plus its prefetch is a few hundred rows, so this
    /// is headroom rather than a limit a client should feel — and it bounds the frame the same way
    /// <c>GetRunPlanStreamHandler.RowsPerChunk</c> does, well inside the 16 MiB protocol cap.</summary>
    private const int MaxPageRows = 4096;

    public string RequestType => IpcRequestTypes.GetRunPlanPage;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetRunPlanPageRequest)request;
        if (runs.SnapshotDirectory(typed.RunId) is not { } directory)
            return Error("RUN_NOT_FOUND", $"no run with id {typed.RunId}");

        Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(directory);
        if (read.TryGetError(out string? headerError))
            return Error("RUN_PLAN_UNAVAILABLE", headerError);
        read.TryGetValue(out RunSnapshotHeader? header);

        int count = Math.Clamp(typed.Count, 0, MaxPageRows);
        int[] ordinals = ResolveOrdinals(directory, typed.Side, header!, typed.First, count);

        // One converter per page. Directory indices are therefore PAGE-LOCAL, which is what the client
        // wants: it folds each page into its own small store and never has to reconcile one page's
        // directory table with another's.
        DryRunStreamHandler.WireChunkConverter converter = new(recycleWireRecords: false);
        DryRunChunkResponse chunk = typed.Side == RunPlanSide.Sources
            ? SourcePage(directory, ordinals, converter)
            : DestinationPage(directory, header!, ordinals, converter);
        return Task.FromResult<IpcResponse>(chunk);
    }

    private static Task<IpcResponse> Error(string code, string message) =>
        Task.FromResult<IpcResponse>(new ErrorResponse { Code = code, Message = message });

    /// <summary>Display positions → row ordinals. Falls back to the identity mapping (plan order) when
    /// the half has no order file, which is what a pre-paging snapshot looks like.</summary>
    private int[] ResolveOrdinals(
        string directory, RunPlanSide side, RunSnapshotHeader header, int first, int count)
    {
        if (count <= 0 || first < 0)
            return [];
        string orderFile = Path.Combine(
            directory,
            side == RunPlanSide.Sources
                ? RunSnapshotPaths.SourcesOrderFileName
                : RunSnapshotPaths.DestinationsOrderFileName);

        int[] ordered = RunSnapshotOrder.ReadRange(orderFile, first, count, logger);
        if (ordered.Length > 0)
            return ordered;

        // No order file. Only treat it as plan order if there ARE rows to show — an empty result past
        // the end of a real order file must stay empty rather than becoming an unsorted page.
        if (RunSnapshotOrder.RowCount(orderFile, logger) > 0)
            return [];

        int total = side == RunPlanSide.Sources
            ? header.SourceItemCount
            : header.DestinationItemCount + header.DeleteItemCount;
        if (first >= total)
            return [];
        int take = Math.Min(count, total - first);
        int[] identity = new int[take];
        for (int i = 0; i < take; i++)
            identity[i] = first + i;
        return identity;
    }

    /// <summary>Builds the source half of a page. Each row carries its own operation, so the client's
    /// per-source grouping resolves entirely within the page.</summary>
    private DryRunChunkResponse SourcePage(
        string directory, int[] ordinals, DryRunStreamHandler.WireChunkConverter converter)
    {
        RunSourceItem?[] items = ReadRows(
            directory, RunSnapshotPaths.SourcesFileName, RunSnapshotPaths.SourcesIndexFileName,
            ordinals, RunSnapshotJsonContext.Default.RunSourceItem);

        List<PhysicalFile> files = [];
        List<VirtualFileOperation> ops = [];
        foreach (RunSourceItem? item in items)
        {
            if (item is null)
                continue;   // an unreadable row is a hole in the page, never a failed page
            files.Add(new PhysicalFile
            {
                Path = item.Path,
                Root = item.SourceRoot,
                Length = item.SizeBytes,
                LastWritten = item.LastWriteUtc,
            });
            ops.Add(new VirtualFileOperation
            {
                Path = item.Path,
                Root = item.SourceRoot,
                Kind = item.Kind,
                // PAGE-LOCAL, not the plan's ordinal: the client folds this page as a standalone unit,
                // so an operation names its file by position within the page.
                SourceIndex = files.Count - 1,
                SourceDisposition = item.Disposition,
                Detail = item.Detail,
            });
        }
        return converter.Convert(files, [], ops, []);
    }

    /// <summary>Builds the destination half of a page. The two files behind it are one index space to
    /// the client (projection rows, then orphans), so an ordinal below the projection count reads from
    /// <c>destinations.ndjsonl</c> and anything above it from <c>deletes.ndjsonl</c>.
    ///
    /// <para>A destination row's source is deliberately NOT resolved here. Its <c>SourceOrdinal</c> names
    /// a position in the whole plan's source list, which this page does not contain — and following it
    /// would mean a second random read per row. The client shows destination rows on their own terms;
    /// the two tabs line up because both are sorted by the same relative key, not because one indexes
    /// into the other.</para></summary>
    private DryRunChunkResponse DestinationPage(
        string directory, RunSnapshotHeader header, int[] ordinals,
        DryRunStreamHandler.WireChunkConverter converter)
    {
        int projection = header.DestinationItemCount;
        List<int> fromProjection = [];
        List<int> fromDeletes = [];
        foreach (int ordinal in ordinals)
        {
            if (ordinal < projection)
                fromProjection.Add(ordinal);
            else
                fromDeletes.Add(ordinal - projection);
        }

        RunDestinationItem?[] projected = ReadRows(
            directory, RunSnapshotPaths.DestinationsFileName, RunSnapshotPaths.DestinationsIndexFileName,
            fromProjection, RunSnapshotJsonContext.Default.RunDestinationItem);
        RunDeleteItem?[] deletes = ReadRows(
            directory, RunSnapshotPaths.DeletesFileName, RunSnapshotPaths.DeletesIndexFileName,
            fromDeletes, RunSnapshotJsonContext.Default.RunDeleteItem);

        List<PhysicalFile> files = [];
        List<VirtualFileOperation> ops = [];
        int projectionAt = 0, deleteAt = 0;
        foreach (int ordinal in ordinals)
        {
            if (ordinal < projection)
                AddProjection(projected[projectionAt++]);
            else
                AddDelete(deletes[deleteAt++]);
        }
        return converter.Convert([], files, [], ops);

        void AddProjection(RunDestinationItem? item)
        {
            if (item is null)
                return;
            // A row whose subject is null names a path that does not exist yet (New, or a rename's new
            // name): it contributes an operation and no file, exactly as the full replay does.
            int subject = -1;
            if (item.SubjectPath is { } subjectPath)
            {
                files.Add(new PhysicalFile
                {
                    Path = subjectPath,
                    Root = item.TargetRoot,
                    Length = item.SubjectSizeBytes ?? 0,
                    LastWritten = item.SubjectLastWriteUtc ?? default,
                });
                subject = files.Count - 1;
            }
            ops.Add(new VirtualFileOperation
            {
                Path = item.Path,
                Root = item.TargetRoot,
                Kind = item.Kind,
                SourceIndex = -1,   // see the summary: the plan's source ordinal is not page-local
                SubjectIndex = subject,
                Detail = item.Detail,
            });
        }

        void AddDelete(RunDeleteItem? item)
        {
            if (item is null)
                return;
            files.Add(new PhysicalFile
            {
                Path = item.Path,
                Root = item.TargetRoot,
                Length = item.SizeBytes,
                LastWritten = item.LastWriteUtc,
            });
            ops.Add(new VirtualFileOperation
            {
                Path = item.Path,
                Root = item.TargetRoot,
                Kind = OperationKind.Deleted,
                SourceIndex = -1,
                SubjectIndex = files.Count - 1,
            });
        }
    }

    /// <summary>Reads rows by ordinal, seeking through the block index when there is one and scanning
    /// when there is not.</summary>
    private T?[] ReadRows<T>(
        string directory, string dataFile, string indexFile, IReadOnlyList<int> ordinals,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ordinals.Count == 0)
            return [];
        string path = Path.Combine(directory, dataFile);
        if (RunSnapshotBlockIndex.TryRead(Path.Combine(directory, indexFile), logger) is { } index)
            return RunSnapshotReader.ReadByOrdinal(path, index, ordinals, typeInfo, logger);

        // No index: one sequential pass, picking the wanted ordinals out as they go by. Linear in the
        // FILE rather than in the page, which is why the index exists — but correct, and the only thing
        // that makes a pre-paging snapshot still viewable.
        logger.LogDebug(
            "Run snapshot: \"{Path}\" has no block index; serving this page by scanning it", path);
        return RunSnapshotReader.ScanByOrdinal(path, ordinals, typeInfo, logger);
    }
}
