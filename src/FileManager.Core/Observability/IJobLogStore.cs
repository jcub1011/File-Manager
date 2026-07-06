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
}

public sealed record JobSummary(
    Guid JobId, Guid ProfileId, string SourcePath, JobOutcome Outcome,
    SkipReason? SkipReason, DateTimeOffset StartedAtUtc, TimeSpan Duration);
