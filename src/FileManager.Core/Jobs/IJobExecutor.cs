using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Jobs;

public interface IJobExecutor
{
    /// <summary>Runs one job to a terminal outcome. <paramref name="progress"/> is optional
    /// decoration: when supplied it receives a <see cref="JobProgress"/> sample as each §4.3 phase is
    /// entered and as each target settles. The executor never lets a throwing sink affect a job's
    /// outcome, and never reports after returning.</summary>
    Task<JobCompletion> ExecuteAsync(
        JobPlan plan, IProgress<JobProgress>? progress = null, CancellationToken ct = default);
}
