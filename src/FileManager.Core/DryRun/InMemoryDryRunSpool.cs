using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>A pure in-memory spool: writes accumulate in a list, reads replay it. Used by the engine's
/// unit tests (no disk) and as the default when no factory is injected. Thread-safe writes via a lock;
/// contention is negligible next to the evaluation work each write follows.</summary>
internal sealed class InMemoryDryRunSpool : IDryRunSpool
{
    private readonly List<FileEvaluation> _entries = [];
    private readonly Lock _gate = new();

    public ValueTask WriteAsync(FileEvaluation evaluation, CancellationToken ct)
    {
        lock (_gate)
            _entries.Add(evaluation);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteWritingAsync() => ValueTask.CompletedTask;

    public async IAsyncEnumerable<IEvaluationView> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Snapshot the list length under the lock is unnecessary — writes are complete before reads.
        // The originals are single-allocation and shared, so they replay as-is (no pooling): their
        // Recycle is a no-op, so the engine's per-chunk recycle leaves them untouched.
        foreach (FileEvaluation entry in _entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _entries.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out <see cref="InMemoryDryRunSpool"/>s. The engine's default factory.</summary>
internal sealed class InMemoryDryRunSpoolFactory : IDryRunSpoolFactory
{
    public IDryRunSpool Create() => new InMemoryDryRunSpool();
}
