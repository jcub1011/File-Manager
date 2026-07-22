using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Core.Observability;

/// <summary>Per-job drill-down logs (spec §7, §9): one <c>logs/jobs/&lt;job-id&gt;.log</c> per job for
/// the GUI activity view (transformer output, retries, verification, skip reasons), plus an
/// in-memory ring of the most recent <see cref="JobSummary"/> for get-recent-jobs. The summary ring
/// is not persisted in v1 (empty after a restart); the per-job log files persist.</summary>
public sealed class JobLogStore(EnginePaths paths, TimeProvider time, ILogger<JobLogStore> logger) : IJobLogStore
{
    private const int MaxRecentSummaries = 500;

    // File writes are serialized: a job's parallel target tasks can append concurrently, and
    // File.AppendAllText opens the file per call, so two racing appends would collide without this.
    private readonly object _fileGate = new();
    private readonly object _ringGate = new();
    private readonly LinkedList<JobSummary> _recent = new();

    public Result Append(Guid jobId, string line)
    {
        string path = LogPathFor(jobId);
        string stamped = $"{time.GetUtcNow():O} {line}{Environment.NewLine}";
        try
        {
            lock (_fileGate)
            {
                Directory.CreateDirectory(paths.JobLogsDirectory);
                File.AppendAllText(path, stamped);
            }
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not append to the job log for {JobId}", jobId);
            return $"could not append to the job log: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: job logging is best-effort and must never surface as a job failure.
            logger.LogError(ex, "Unexpected error appending to the job log for {JobId}", jobId);
            return $"could not append to the job log: {ex.Message}";
        }
    }

    public Result<IReadOnlyList<string>, string> Read(Guid jobId)
    {
        string path = LogPathFor(jobId);
        try
        {
            if (!File.Exists(path))
                return $"no log for job {jobId}";
            string[] lines = File.ReadAllLines(path);
            return Result<IReadOnlyList<string>, string>.Success(lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not read the job log: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log.
            logger.LogError(ex, "Unexpected error reading the job log for {JobId}", jobId);
            return $"could not read the job log: {ex.Message}";
        }
    }

    public Result<IReadOnlyList<JobSummary>, string> ListRecent(int count)
    {
        if (count < 0)
            count = 0;
        lock (_ringGate)
        {
            List<JobSummary> result = new(Math.Min(count, _recent.Count));
            foreach (JobSummary summary in _recent)   // newest first (AddFirst)
            {
                if (result.Count >= count)
                    break;
                result.Add(summary);
            }
            return Result<IReadOnlyList<JobSummary>, string>.Success(result);
        }
    }

    public void RecordSummary(JobSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        lock (_ringGate)
        {
            _recent.AddFirst(summary);
            while (_recent.Count > MaxRecentSummaries)
                _recent.RemoveLast();
        }
    }

    private string LogPathFor(Guid jobId) =>
        Path.Combine(paths.JobLogsDirectory, jobId.ToString("N") + ".log");
}
