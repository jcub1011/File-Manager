using FileManager.Contracts.DryRun;
using System.Collections.Generic;

namespace FileManager.Core.DryRun;

/// <summary>Per-sweep pool of the streamed destination sweep's chunk carriers — the sweep analogue of
/// <see cref="EvaluationCarrierPool"/> (which serves the source phase's spilled replay and stays
/// untouched). Created per <c>SweepStreamAsync</c> call and dropped with it, so nothing here outlives
/// a run.
///
/// <para><b>Why it exists:</b> the sweep used to allocate a fresh <c>PhysicalFile</c> +
/// <c>VirtualFileOperation</c> per pre-existing destination file — none of it live past its chunk, but
/// at 500k swept entries that is ~75 MB of churn the GC must absorb during the burst, and in-run peak
/// commit tracks churn. Renting the existing pooled carriers instead caps the sweep's record
/// allocations at roughly (channel depth + 1) chunks' worth, independent of entry count.</para>
///
/// <para><b>Unlike <see cref="EvaluationCarrierPool"/> this one is thread-safe:</b> the producer
/// (<c>ProduceChunksAsync</c>, a pool thread) rents while the consumer (the async reader) recycles.
/// One uncontended lock per carrier pair on the rent side and one per chunk on the recycle side —
/// the recycle only ever runs between the producer's channel writes.</para>
///
/// <para><b>Ownership contract:</b> a yielded chunk's carriers belong to the pool and are valid only
/// until the consumer requests the next chunk (see <c>SweepStreamAsync</c>'s doc). Recycling past the
/// retention cap drops the carrier for the GC — over-recycling never hands an instance out twice. A
/// consumer that abandons the stream simply never recycles; the pool dies with the run.</para></summary>
internal sealed class SweepCarrierPool
{
    /// <summary>Comfortably above the carriers of (channel depth + 1) chunks at the 48 KiB wire
    /// budget (a few hundred records each), so steady-state recycling never drops; small enough that
    /// the pool itself stays a constant. Same figure and rationale as
    /// <see cref="EvaluationCarrierPool"/>.</summary>
    private const int MaxRetainedPerKind = 2_048;

    /// <summary>Chunk lists in flight: at most the channel's buffer plus the one being filled and the
    /// one being consumed.</summary>
    private const int MaxRetainedLists = 8;

    private readonly object _gate = new();
    private readonly Stack<PooledPhysicalFile> _files = new();
    private readonly Stack<PooledFileOperation> _ops = new();
    private readonly Stack<List<IPhysicalFileView>> _fileLists = new();
    private readonly Stack<List<IFileOperationView>> _opLists = new();

    /// <summary>(file, op) pairs handed out / recycled over the pool's life. Equal after a fully
    /// consumed stream — the borrow==return property the tests assert (mirroring
    /// <c>Spilled_stream_returns_every_rented_carrier_to_the_pool</c>).</summary>
    public long RentedPairs { get; private set; }
    public long ReturnedPairs { get; private set; }

    /// <summary>Carriers/lists currently held for reuse — bounded, never proportional to the swept
    /// entry count.</summary>
    public int RetainedCount
    {
        get
        {
            lock (_gate)
                return _files.Count + _ops.Count + _fileLists.Count + _opLists.Count;
        }
    }

    public (PooledPhysicalFile File, PooledFileOperation Op) RentPair()
    {
        lock (_gate)
        {
            RentedPairs++;
            return (
                _files.Count > 0 ? _files.Pop() : new PooledPhysicalFile(),
                _ops.Count > 0 ? _ops.Pop() : new PooledFileOperation());
        }
    }

    public List<IPhysicalFileView> RentFileList()
    {
        lock (_gate)
            return _fileLists.Count > 0 ? _fileLists.Pop() : [];
    }

    public List<IFileOperationView> RentOpList()
    {
        lock (_gate)
            return _opLists.Count > 0 ? _opLists.Pop() : [];
    }

    /// <summary>Returns everything a fully consumed chunk owns: its carriers and, when they are the
    /// pool's own lists, the two list instances. Tolerates non-pool content (the trailing SweepCapped
    /// marker carries empty arrays) so the caller can recycle every yielded chunk uniformly.</summary>
    public void Recycle(DryRunChunk chunk)
    {
        lock (_gate)
        {
            foreach (IPhysicalFileView f in chunk.DestinationFiles)
            {
                if (f is not PooledPhysicalFile file)
                    continue;
                ReturnedPairs++;
                // Clear the strings BEFORE the cap check, same rationale as EvaluationCarrierPool:
                // a retained carrier must not pin its (unique) path for the rest of the run, and a
                // dropped one must actually release it.
                file.Path = "";
                file.Root = "";
                if (_files.Count < MaxRetainedPerKind)
                    _files.Push(file);
            }
            foreach (IFileOperationView o in chunk.DestinationOperations)
            {
                if (o is not PooledFileOperation op)
                    continue;
                op.Path = "";
                op.Root = "";
                op.Detail = null;
                if (_ops.Count < MaxRetainedPerKind)
                    _ops.Push(op);
            }
            if (chunk.DestinationFiles is List<IPhysicalFileView> fileList)
            {
                fileList.Clear();
                if (_fileLists.Count < MaxRetainedLists)
                    _fileLists.Push(fileList);
            }
            if (chunk.DestinationOperations is List<IFileOperationView> opList)
            {
                opList.Clear();
                if (_opLists.Count < MaxRetainedLists)
                    _opLists.Push(opList);
            }
        }
    }
}
