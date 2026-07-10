using FileManager.Contracts.Primitives;
using FileManager.Core.Audit;
using FileManager.Core.Jobs;
using System;

namespace FileManager.Core.Disposition;

public interface ISourceDispositionService
{
    Result<DispositionAuditRecord, JobError> Dispose(JobExecution execution);

    /// <summary>Journal-driven variant for crash recovery's post-commit branch (§7.3 row I), which
    /// has no live <see cref="JobExecution"/> — everything comes from the <c>job-opened</c> record.
    /// Additive to the doc's single-method surface (the plan's resolved design note).</summary>
    Result<DispositionAuditRecord, JobError> Dispose(
        Guid jobId, SourceSnapshot source, PolicySnapshot policies, bool anyTargetSkippedConflict);
}
