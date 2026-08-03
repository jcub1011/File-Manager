using System;
using FileManager.Contracts.DryRun;

namespace FileManager.Contracts.IPC;

/// <summary>Consumes a streamed dry run one frame at a time, so a consumer that does not need the
/// assembled <see cref="DryRunReport"/> never pays for one.
///
/// <para>Why this exists: the list-accumulating <c>DryRunStreamAsync</c> overload holds every
/// record of the run — at the 500k cap that is ~2M wire objects — and the UI then projects a second
/// full copy out of it, so both are live at once. A sink lets the UI fold each chunk into its own
/// columnar store and drop the chunk, which removes the report from the client's heap entirely.
/// The assembling overload is itself written on top of this one (<c>ReportAssemblySink</c>), so
/// there is exactly one reassembly loop and one trust-boundary check.</para>
///
/// <para>Contract for implementers: <see cref="OnChunk"/> is called once per received chunk frame,
/// in receive order, on the thread pumping the stream. The chunk and every record in it belong to
/// the caller and are <b>not</b> valid after the call returns — copy out what you need. Indices are
/// global: a file/op's <c>DirIndex</c>/<c>RootDirIndex</c> is a position in the directory table
/// assembled across all chunks so far, and an op's <c>SourceIndex</c>/<c>SubjectIndex</c> is a
/// position in the fully assembled file lists, so a sink must append in the order it is called.
/// Chunks are validated against the trust boundary before the sink sees them (parents precede
/// children; every directory reference is in range).</para>
///
/// <para>Throwing from <see cref="OnChunk"/> aborts the stream and surfaces as an
/// <c>IPC_SINK_FAILED</c> error, leaving the connection poisoned — it is a bug in the sink, not a
/// protocol fault, and the connection has unread frames queued on it either way.</para></summary>
public interface IDryRunChunkSink
{
    /// <summary>Folds one received chunk in. The chunk is not valid after this returns.</summary>
    void OnChunk(DryRunChunkResponse chunk);
}

/// <summary>What the terminating <see cref="DryRunCompleteResponse"/> carried — the run-level facts
/// that are not per-file and so do not go through <see cref="IDryRunChunkSink"/>. Returned by the
/// sink overload of <c>IpcClient.DryRunStreamAsync</c> in place of the assembled report.</summary>
public sealed record DryRunCompletion(DateTimeOffset GeneratedAt, bool Truncated, SpaceProjection? Space);
