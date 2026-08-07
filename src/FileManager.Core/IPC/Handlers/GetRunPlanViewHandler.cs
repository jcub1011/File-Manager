using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Runs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Narrows a plan half to the rows matching a filter, and answers with a handle to page
/// against plus the row count.
///
/// <para>The counterpart to <see cref="GetRunPlanPageHandler"/>: that one answers "what is at position
/// N", this one answers "how many positions are there, and of what". Together they are what lets a
/// client show a filtered, sorted, arbitrarily large plan while holding a viewport — the search box, the
/// facet bar and the status chips all used to require every row to be resident, which is precisely the
/// cost that stopped being bounded when the plan lost its file cap.</para>
///
/// <para>A filter that selects everything is answered with a null view id and the plan's own totals, at
/// no scan cost — which is the state a freshly opened preview is in.</para></summary>
public sealed class GetRunPlanViewHandler(
    IRunCoordinator runs, ILogger<GetRunPlanViewHandler> logger) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetRunPlanView;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetRunPlanViewRequest)request;
        if (runs.SnapshotDirectory(typed.RunId) is not { } directory)
            return Error("RUN_NOT_FOUND", $"no run with id {typed.RunId}");

        Result<RunSnapshotHeader, string> read = RunSnapshotReader.ReadHeader(directory);
        if (read.TryGetError(out string? headerError))
            return Error("RUN_PLAN_UNAVAILABLE", headerError);
        read.TryGetValue(out RunSnapshotHeader? header);

        RunSnapshotView.Filter filter = new()
        {
            Search = string.IsNullOrWhiteSpace(typed.Search) ? null : typed.Search.Trim(),
            SourceRoots = ToSet(typed.SourceRoots),
            DestinationRoots = ToSet(typed.DestinationRoots),
            Kinds = ToKindSet(typed.Kinds),
            IncludeDestructiveDisposition = typed.IncludeDestructiveDisposition,
        };

        if (RunSnapshotView.Build(directory, typed.Side, header!, filter, logger) is { } view)
            return Ok(view.ViewId, view.RowCount);

        // No filter: the whole half, in the plan's own order.
        int total = typed.Side == RunPlanSide.Sources
            ? header!.SourceItemCount
            : header!.DestinationItemCount + header.DeleteItemCount;
        return Ok(viewId: null, total);
    }

    private static Task<IpcResponse> Ok(string? viewId, int rowCount) =>
        Task.FromResult<IpcResponse>(new RunPlanViewResponse { ViewId = viewId, RowCount = rowCount });

    private static Task<IpcResponse> Error(string code, string message) =>
        Task.FromResult<IpcResponse>(new ErrorResponse { Code = code, Message = message });

    /// <summary>Null stays null — "no opinion", which keeps every root. An EMPTY list is honoured as an
    /// empty set and keeps nothing, because that is a filter the user can actually express (deselecting
    /// every facet) and silently widening it to "everything" would be a lie.</summary>
    private static IReadOnlySet<string>? ToSet(IReadOnlyList<string>? values) =>
        values is null ? null : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<OperationKind>? ToKindSet(IReadOnlyList<OperationKind>? values) =>
        values is null ? null : new HashSet<OperationKind>(values);
}
