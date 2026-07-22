using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Observability;

public interface IJobLogStore
{
    Result Append(Guid jobId, string line);
    Result<IReadOnlyList<string>, string> Read(Guid jobId);
    Result<IReadOnlyList<JobSummary>, string> ListRecent(int count);

    /// <summary>Records a finished job's summary into the recent-jobs ring (fed by the orchestrator
    /// on completion). Separate from <see cref="Append"/> because the ring is the get-recent-jobs
    /// source while the per-job log file is the get-job-log source. The ring is in-memory only in
    /// v1 — empty after a restart; the log files persist (§4.10 amendment).</summary>
    void RecordSummary(JobSummary summary);
}

public sealed record JobSummary(
    Guid JobId, Guid ProfileId, string SourcePath, JobOutcome Outcome,
    SkipReason? SkipReason, DateTimeOffset StartedAtUtc, TimeSpan Duration);
