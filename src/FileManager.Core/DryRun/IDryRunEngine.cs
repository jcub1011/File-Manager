using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

public interface IDryRunEngine
{
    Task<Result<DryRunReport, string>> SimulateAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default);

    /// <summary>Streams the report as source-path-ordered chunks (no single-frame size ceiling). A
    /// fatal setup/scan error is a single failure item that ends the stream; cancellation surfaces
    /// as <see cref="OperationCanceledException"/> from the enumerator.</summary>
    IAsyncEnumerable<Result<IReadOnlyList<DryRunFileResult>, string>> SimulateStreamAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default);
}
