using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Jobs;

/// <summary>Consumes <see cref="ITriggerQueue"/> on a bounded worker pool (<c>MaxWorkers</c>, §8),
/// builds a <see cref="JobPlan"/> per payload, runs it through <see cref="IJobExecutor"/>, and
/// publishes lifecycle events to <see cref="IEngineEventBus"/>. A started job is never cancelled by
/// shutdown (I-ATOMIC-JOB): the executor runs under <see cref="CancellationToken.None"/> and
/// <see cref="StopAsync"/> stops dequeuing then awaits in-flight jobs to completion.</summary>
public sealed class JobOrchestrator(
    ITriggerQueue queue,
    IProfileCatalog catalog,
    IJobExecutor executor,
    JobPlanFactory planFactory,
    IEngineEventBus eventBus,
    IJobLogStore jobLog,
    IPauseStateService pauseState,
    EngineConfig config,
    TimeProvider time,
    ILogger<JobOrchestrator> logger) : IJobOrchestrator
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    private CancellationTokenSource? _cts;
    private SemaphoreSlim? _workers;
    private Task? _consumer;
    private int _jobsInFlight;
    private volatile string? _lastError;

    public Result Start()
    {
        if (_consumer is not null)
            return "the job orchestrator is already running";

        int maxWorkers = config.MaxWorkers > 0 ? config.MaxWorkers : Environment.ProcessorCount;
        _cts = new CancellationTokenSource();
        _workers = new SemaphoreSlim(maxWorkers, maxWorkers);
        _consumer = Task.Run(() => ConsumeAsync(_cts.Token));
        logger.LogInformation("Job orchestrator started with {MaxWorkers} workers", maxWorkers);
        return Result.Success();
    }

    public async Task StopAsync()
    {
        if (_cts is null || _consumer is null)
            return;

        _cts.Cancel();
        try
        {
            await _consumer.ConfigureAwait(false);                       // stop dequeuing
            await Task.WhenAll(_running.Keys).ConfigureAwait(false);     // drain in-flight (I-ATOMIC-JOB)
        }
        catch (OperationCanceledException)
        {
            // expected as the consumer's DequeueAsync observes the cancelled token
        }
        _cts.Dispose();
        _cts = null;
        _workers?.Dispose();
        _workers = null;
        _consumer = null;
        logger.LogInformation("Job orchestrator stopped");
    }

    public EngineStatusSnapshot GetStatus() => new(
        Paused: pauseState.IsPaused,
        ActiveProfiles: catalog.Active.Count,
        JobsInFlight: Volatile.Read(ref _jobsInFlight),
        QueuedPayloads: queue.PendingCount,
        LastError: _lastError);

    private async Task ConsumeAsync(CancellationToken ct)
    {
        SemaphoreSlim workers = _workers!;
        try
        {
            await foreach (Payload payload in queue.DequeueAsync(ct).ConfigureAwait(false))
            {
                await workers.WaitAsync(ct).ConfigureAwait(false);

                Task job = Task.Run(() => RunJobAsync(payload, workers), CancellationToken.None);
                _running.TryAdd(job, 0);
                _ = job.ContinueWith(
                    t => _running.TryRemove(t, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown — the queue's DequeueAsync / WaitAsync observed the cancelled token
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: the consumer loop is otherwise unobserved until StopAsync;
            // without this the engine silently stops picking up work.
            logger.LogCritical(ex, "Job orchestrator consumer loop failed unexpectedly; no further jobs will run");
        }
    }

    private async Task RunJobAsync(Payload payload, SemaphoreSlim workers)
    {
        try
        {
            Profile? profile = catalog.All.FirstOrDefault(p => p.Id == payload.ProfileId);
            if (profile is null)
            {
                logger.LogInformation("Dropping payload for {SourcePath}: profile {ProfileId} no longer exists",
                    payload.SourcePath, payload.ProfileId);
                return;
            }

            Result<JobPlan, JobError> planResult = planFactory.Build(profile, payload);
            if (planResult.TryGetError(out JobError? planError))
            {
                logger.LogInformation("Dropping payload for {SourcePath}: {Error}", payload.SourcePath, planError.Message);
                return;
            }
            planResult.TryGetValue(out JobPlan? plan);

            DateTimeOffset startedAt = time.GetUtcNow();
            eventBus.Publish(new JobStartedEvent
            {
                AtUtc = startedAt,
                JobId = plan!.JobId.Value,
                ProfileId = plan.ProfileId,
                SourcePath = plan.Source.Path,
            });
            Interlocked.Increment(ref _jobsInFlight);

            JobCompletion completion;
            try
            {
                // CancellationToken.None: a started job is never cancelled by shutdown (I-ATOMIC-JOB);
                // StopAsync awaits it instead.
                completion = await executor.ExecuteAsync(plan, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _jobsInFlight);
            }

            PublishCompletion(profile, plan, completion, startedAt);
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log: RunJobAsync is fire-and-forget; a throw here must never
            // escape (the executor already never throws, so this is defence in depth).
            logger.LogError(ex, "Unexpected error running the job for {SourcePath}", payload.SourcePath);
            _lastError = ex.Message;
        }
        finally
        {
            workers.Release();
        }
    }

    private void PublishCompletion(Profile profile, JobPlan plan, JobCompletion completion, DateTimeOffset startedAt)
    {
        JobSummary summary = new(
            plan.JobId.Value, plan.ProfileId, plan.Source.Path, completion.Outcome,
            completion.SkipReason, startedAt, completion.Duration);
        jobLog.RecordSummary(summary);

        JobSummaryDto dto = new(
            plan.JobId.Value, plan.ProfileId, plan.Source.Path, completion.Outcome.ToString(),
            completion.SkipReason?.ToString(), startedAt, completion.Duration);
        DateTimeOffset now = time.GetUtcNow();

        if (completion.Outcome is JobOutcome.Failed or JobOutcome.RollbackFailed)
        {
            _lastError = completion.Error?.Message;
            eventBus.Publish(new JobFailedEvent
            {
                AtUtc = now,
                Job = dto,
                Error = completion.Error?.Message ?? "unknown error",
                NotifyOnFailure = profile.Logging.NotifyOnFailure,
            });
            logger.LogWarning("Job {JobId} for {SourcePath} ended {Outcome}: {Error}",
                plan.JobId.Short, plan.Source.Path, completion.Outcome, completion.Error?.Message);
        }
        else
        {
            eventBus.Publish(new JobCompletedEvent { AtUtc = now, Job = dto });
            logger.LogInformation("Job {JobId} for {SourcePath} ended {Outcome}",
                plan.JobId.Short, plan.Source.Path, completion.Outcome);
        }
    }
}
