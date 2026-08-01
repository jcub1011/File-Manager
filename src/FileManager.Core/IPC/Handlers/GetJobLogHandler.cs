using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Observability;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Handles get-job-log (§4.9, spec §7): the per-job drill-down log lines for the activity
/// view. A missing log (unknown job) is an error, not an empty success — and it is a DIFFERENT error
/// from a log that exists but cannot be read: the GUI renders the former as the benign "skipped before
/// any work began" and the latter as a failure the user should see.</summary>
public sealed class GetJobLogHandler(IJobLogStore jobLog) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetJobLog;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetJobLogRequest)request;
        Result<IReadOnlyList<string>, JobLogReadError> log = jobLog.Read(typed.JobId);
        if (log.TryGetError(out JobLogReadError? error))
        {
            IpcResponse failure = new ErrorResponse
            {
                Code = error.Reason == JobLogReadFailure.NotFound ? "JOB_LOG_NOT_FOUND" : "JOB_LOG_UNREADABLE",
                Message = error.Message,
            };
            return Task.FromResult(failure);
        }
        log.TryGetValue(out IReadOnlyList<string>? lines);
        IpcResponse response = new JobLogResponse { Lines = lines! };
        return Task.FromResult(response);
    }
}
