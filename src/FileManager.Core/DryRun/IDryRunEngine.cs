using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

public interface IDryRunEngine
{
    Task<Result<DryRunReport, string>> SimulateAsync(
        Guid profileId, string? scopePath, CancellationToken ct = default);
}
