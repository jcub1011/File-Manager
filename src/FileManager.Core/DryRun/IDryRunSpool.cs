using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>A write-once, read-once spool for a single dry-run's evaluated findings, sitting between
/// the engine's scan/evaluate pipeline and the chunk emitter. The engine writes every
/// <see cref="FileEvaluation"/> as it is produced (discovery order — no global sort), signals
/// <see cref="CompleteWritingAsync"/>, then replays them with <see cref="ReadAllAsync"/> to build the
/// streamed chunks. Decoupling the two phases through the spool lets the scan finish (and release its
/// filesystem handles) before the UI has consumed anything, and — for a large scan — keeps the whole
/// evaluated set off the managed heap. The file-backed implementation keeps small runs in memory and
/// only spills to disk past a threshold. Disposal releases/deletes any backing storage.</summary>
internal interface IDryRunSpool : System.IAsyncDisposable
{
    /// <summary>Appends one evaluated finding. Thread-safe: the evaluation workers call this
    /// concurrently from <c>Parallel.ForEachAsync</c>.</summary>
    ValueTask WriteAsync(FileEvaluation evaluation, CancellationToken ct);

    /// <summary>Signals that no more writes will arrive and flushes the backing store. Must be awaited
    /// before <see cref="ReadAllAsync"/>. Rethrows any error the backing writer hit.</summary>
    ValueTask CompleteWritingAsync();

    /// <summary>Replays every written finding in write (discovery) order. Call once, after
    /// <see cref="CompleteWritingAsync"/>.</summary>
    IAsyncEnumerable<FileEvaluation> ReadAllAsync(CancellationToken ct);
}

/// <summary>Creates a fresh <see cref="IDryRunSpool"/> per dry-run. Injected into the engine so tests
/// use an in-memory spool while the service wires the disk-backed one.</summary>
internal interface IDryRunSpoolFactory
{
    IDryRunSpool Create();
}
