using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Contracts.Primitives;
using System.Collections.Generic;
using System.Threading;

namespace FileManager.Core.Watching;

public interface ISourceScanner
{
    /// <remarks>Emission order is UNSPECIFIED: the walk runs on the shared scan scheduler's worker
    /// threads, so payloads and faults arrive in whatever order the concurrent enumeration produces
    /// them — it is not depth-first and is not stable across runs. A consumer that needs deterministic
    /// output (e.g. a report) must sort by <see cref="Payload.SourcePath"/> itself;
    /// <see cref="DryRun.DryRunEngine"/> does exactly this. One consequence for callers that stop early
    /// (a file cap): because a fatal walk-root fault can be produced after other roots' payloads,
    /// hitting the cap first may leave that fault unobserved — a caller that must not miss it has to
    /// drain the whole sequence.</remarks>
    /// <param name="ct">Cancels the walk; breaking out of the returned sequence early also tears the
    /// scan session down.</param>
    IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null, CancellationToken ct = default);
}
