using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Observability;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Handles get-recent-jobs (§4.9, spec §7): the recent-jobs ring for the GUI activity view.
/// The Core <see cref="JobSummary"/> enum fields map to the wire DTO's string fields.</summary>
public sealed class GetRecentJobsHandler(IJobLogStore jobLog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetRecentJobs;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetRecentJobsRequest)request;
        Result<IReadOnlyList<JobSummary>, string> recent = jobLog.ListRecent(typed.Count);
        if (recent.TryGetError(out string? error))
        {
            IpcResponse failure = new ErrorResponse { Code = "RECENT_JOBS_FAILED", Message = error };
            return Task.FromResult(failure);
        }
        recent.TryGetValue(out IReadOnlyList<JobSummary>? jobs);
        List<JobSummaryDto> dtos = new(jobs!.Count);
        foreach (JobSummary job in jobs)
            dtos.Add(new JobSummaryDto(
                job.JobId, job.ProfileId, job.SourcePath, job.Outcome.ToString(),
                job.SkipReason?.ToString(), job.StartedAtUtc, job.Duration));
        IpcResponse response = new RecentJobsResponse { Jobs = dtos };
        return Task.FromResult(response);
    }
}
