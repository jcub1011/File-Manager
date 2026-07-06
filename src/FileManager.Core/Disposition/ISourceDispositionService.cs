using FileManager.Contracts.Primitives;
using FileManager.Core.Audit;
using FileManager.Core.Jobs;

namespace FileManager.Core.Disposition;

public interface ISourceDispositionService
{
    Result<DispositionAuditRecord, JobError> Dispose(JobExecution execution);
}
