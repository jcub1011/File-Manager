using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

public interface ITransientRetryPolicy
{
    Task<Result<TValue, JobError>> ExecuteAsync<TValue>(
        string operationName,
        Func<CancellationToken, Task<Result<TValue, JobError>>> operation,
        CancellationToken ct = default);
}
