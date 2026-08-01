using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Observability;

public interface IJobLogStore
{
    Result Append(Guid jobId, string line);

    /// <summary>Reads a job's log lines. The error is TYPED because the two failures mean opposite
    /// things to a reader: "this job has no log" is normal (it was skipped before its journal opened),
    /// while "the log exists but could not be read" is a real fault. Collapsing both into one code let
    /// the activity view tell users a job never started work when its log was merely locked.</summary>
    Result<IReadOnlyList<string>, JobLogReadError> Read(Guid jobId);

    Result<IReadOnlyList<JobSummary>, string> ListRecent(int count);

    /// <summary>Records a finished job's summary into the recent-jobs ring (fed by the orchestrator
    /// on completion). Separate from <see cref="Append"/> because the ring is the get-recent-jobs
    /// source while the per-job log file is the get-job-log source. The ring is in-memory only in
    /// v1 — empty after a restart; the log files persist (§4.10 amendment).</summary>
    void RecordSummary(JobSummary summary);
}

/// <summary>Why a job log could not be returned.</summary>
public enum JobLogReadFailure
{
    /// <summary>No log file for this job — normal for a job skipped before any work began.</summary>
    NotFound,

    /// <summary>The log exists but could not be read (locked, permissions, corrupt).</summary>
    Unreadable,
}

public sealed record JobLogReadError(JobLogReadFailure Reason, string Message);

public sealed record JobSummary(
    Guid JobId, Guid ProfileId, string SourcePath, JobOutcome Outcome,
    SkipReason? SkipReason, DateTimeOffset StartedAtUtc, TimeSpan Duration);
