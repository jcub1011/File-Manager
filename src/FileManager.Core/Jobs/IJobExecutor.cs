using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Jobs;

public interface IJobExecutor
{
    Task<JobCompletion> ExecuteAsync(JobPlan plan, CancellationToken ct = default);
}
