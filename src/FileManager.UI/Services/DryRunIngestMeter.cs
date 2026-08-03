using FileManager.Contracts.IPC;
using System;

namespace FileManager.UI.Services;

/// <summary>Wraps the real chunk sink to split a streamed dry run's allocation into the phases that have
/// different fixes, so the next optimization targets a measured number instead of a subtraction.
///
/// <para><strong>Why.</strong> Measured on the published exe (2026-08-03, 33,449 source files / 330,797
/// destination operations): a run allocates <strong>605 MB</strong> in the UI process. Only part of that
/// is accounted for — <c>PrepareReport</c> benchmarks at ~147 MB, and that is at the 500k cap, so at this
/// run's scale it is well under half of it. Everything else was known only as "the rest", which is not
/// something you can optimize. For comparison the <em>service</em> produced and serialized the same run
/// in 292 MB, so the UI allocates roughly twice as much to consume a run as the service does to produce
/// it.</para>
///
/// <para><strong>What the split buys.</strong> The two remaining phases have unrelated fixes.
/// <em>Transport + deserialize</em> is `IpcFrameCodec.ReadFrameAsync` allocating a fresh
/// <c>byte[length]</c> (plus a header array) per frame and <c>IpcSerializer.DeserializeResponse</c>
/// materializing every wire record and its strings — pooling and a reader-based fold respectively.
/// <em>Ingest</em> is <c>DryRunRowStore.OnChunk</c> folding those records into columns, which is already
/// close to the floor. Knowing the ratio decides which is worth touching. Note
/// <c>docs/dry-run-service-memory.md</c> §11 rejected pooling the frame read path "for no
/// <em>service</em>-side benefit" — that rejection was scoped to the service, and this is the consumer
/// that would benefit.</para>
///
/// <para>Costs nothing when logging is below Information: the counters are two long adds per chunk and
/// <see cref="GC.GetTotalAllocatedBytes"/> is a read of per-thread counters, but the whole wrapper is
/// only installed when the level is enabled.</para></summary>
internal sealed class DryRunIngestMeter(IDryRunChunkSink inner) : IDryRunChunkSink
{
    private readonly IDryRunChunkSink _inner = inner;

    public int Chunks { get; private set; }
    public long SourceFiles { get; private set; }
    public long DestinationFiles { get; private set; }
    public long Operations { get; private set; }
    public long Directories { get; private set; }

    /// <summary>Bytes allocated inside <see cref="OnChunk"/> across the whole stream — i.e. the store's
    /// folding cost, isolated from the transport and deserialization that produced the chunk.</summary>
    public long IngestAllocatedBytes { get; private set; }

    public void OnChunk(DryRunChunkResponse chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Chunks++;
        SourceFiles += chunk.SourceFiles.Count;
        DestinationFiles += chunk.DestinationFiles.Count;
        Operations += chunk.SourceOperations.Count + chunk.DestinationOperations.Count;
        Directories += chunk.DirectoryName.Count;

        // Measured around the inner call only. This runs on the stream pump thread and the pump is
        // single-threaded (IDryRunChunkSink's contract), so a plain read-subtract-read is exact here —
        // GetTotalAllocatedBytes' imprecise overload would not be.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        _inner.OnChunk(chunk);
        IngestAllocatedBytes += GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>Writes the split. <paramref name="streamAllocatedBytes"/> is everything the stream cost
    /// (transport + deserialize + ingest), so the transport/deserialize share is the remainder — the
    /// number this class exists to expose.</summary>
    public void Log(long streamAllocatedBytes)
    {
        long transportAndDeserialize = streamAllocatedBytes - IngestAllocatedBytes;
        Serilog.Log.Information(
            "Dry-run stream allocation for {Chunks:N0} chunks ({SourceFiles:N0} source files, " +
            "{DestinationFiles:N0} destination files, {Operations:N0} operations, {Directories:N0} directories): " +
            "total {TotalMb}MB = transport+deserialize {TransportMb}MB ({TransportPct}%) + ingest {IngestMb}MB ({IngestPct}%); " +
            "{BytesPerRecord:N0} bytes allocated per wire record",
            Chunks, SourceFiles, DestinationFiles, Operations, Directories,
            streamAllocatedBytes >> 20, transportAndDeserialize >> 20,
            streamAllocatedBytes == 0 ? 0 : 100 * transportAndDeserialize / streamAllocatedBytes,
            IngestAllocatedBytes >> 20,
            streamAllocatedBytes == 0 ? 0 : 100 * IngestAllocatedBytes / streamAllocatedBytes,
            SourceFiles + DestinationFiles + Operations + Directories is var records && records > 0
                ? streamAllocatedBytes / records
                : 0);
    }
}
