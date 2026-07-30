using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
using FileManager.Core.Tests.TestSupport;
using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Jobs;

public sealed class JobOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-orch-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir;
    private readonly string _sourceFile;
    private readonly Profile _profile;
    private readonly TriggerQueue _queue;
    private readonly EngineEventBus _bus;
    private readonly FakeJobExecutor _executor = new();
    private readonly List<EngineEvent> _events = [];
    private readonly IDisposable _eventSub;
    private readonly JobOrchestrator _orchestrator;

    public JobOrchestratorTests()
    {
        _sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(_sourceDir);
        _sourceFile = Path.Combine(_sourceDir, "file.txt");
        File.WriteAllText(_sourceFile, "content");

        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        Directory.CreateDirectory(paths.JobLogsDirectory);
        EngineConfig config = new();

        _profile = TestProfiles.Valid(_sourceDir, Path.Combine(_root, "target"));
        FakeProfileCatalog catalog = new(_profile);
        _queue = new TriggerQueue(new FakePauseState(), NullLogger<TriggerQueue>.Instance);
        _bus = new EngineEventBus(NullLogger<EngineEventBus>.Instance);
        JobLogStore jobLog = new(paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);
        JobPlanFactory planFactory = new(paths, config);
        _eventSub = _bus.Subscribe(e => { lock (_events) _events.Add(e); });

        _orchestrator = new JobOrchestrator(_queue, catalog, _executor, planFactory, _bus, jobLog,
            new FakePauseState(), config, TimeProvider.System, NullLogger<JobOrchestrator>.Instance);
    }

    private Payload PayloadFor(Guid profileId) =>
        new(profileId, _sourceFile, _sourceDir, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Runs_a_job_and_publishes_started_then_completed()
    {
        Assert.True(_orchestrator.Start().IsSuccess);
        _queue.Enqueue(PayloadFor(_profile.Id));

        Assert.True(await WaitForAsync(() => _executor.Executed.Count == 1, TimeSpan.FromSeconds(5)));
        Assert.True(await WaitForAsync(() => { lock (_events) return _events.Any(e => e is JobCompletedEvent); }, TimeSpan.FromSeconds(5)));

        lock (_events)
        {
            Assert.Contains(_events, e => e is JobStartedEvent);
            Assert.Contains(_events, e => e is JobCompletedEvent);
        }
        await _orchestrator.StopAsync();
    }

    [Fact]
    public async Task Drops_a_payload_for_an_unknown_profile()
    {
        Assert.True(_orchestrator.Start().IsSuccess);
        _queue.Enqueue(PayloadFor(Guid.NewGuid()));   // no such profile

        // Stop FIRST: StopAsync stops dequeuing and then awaits every in-flight job (I-ATOMIC-JOB), so
        // the payload is provably fully processed by the time it returns. A fixed delay proved nothing
        // — it passed on a machine too slow to have started the job yet, and cost 300 ms every run.
        await _orchestrator.StopAsync();
        Assert.Empty(_executor.Executed);
    }

    [Fact]
    public async Task Drops_a_payload_for_an_inactive_profile()
    {
        // §4.3 failure semantics: "a payload whose profile no longer exists/is inactive is dropped
        // with a logged skip". A deactivated profile must never move a file, whatever the trigger.
        Profile inactive = _profile with { Active = false };
        FakeJobExecutor executor = new();
        using TriggerQueue queue = new(new FakePauseState(), NullLogger<TriggerQueue>.Instance);
        JobOrchestrator orchestrator = OrchestratorOver(inactive, executor, queue);

        Assert.True(orchestrator.Start().IsSuccess);
        queue.Enqueue(PayloadFor(inactive.Id));

        await orchestrator.StopAsync();   // drains deterministically — see the sibling test
        Assert.Empty(executor.Executed);
    }

    /// <summary>A consume loop that dies takes the whole pipeline with it — Start() refuses to restart
    /// and every later trigger is accepted and silently never runs. The status snapshot has to say so:
    /// this was the one failure path that logged Critical and left LastError null, so get-status kept
    /// reporting a healthy engine while nothing worked.</summary>
    [Fact]
    public async Task A_dead_consumer_loop_is_reported_in_the_status_snapshot()
    {
        ThrowingTriggerQueue queue = new();
        JobOrchestrator orchestrator = OrchestratorOver(_profile, _executor, queue);

        Assert.True(orchestrator.Start().IsSuccess);
        Assert.True(await WaitForAsync(() => orchestrator.GetStatus().LastError is not null, TimeSpan.FromSeconds(5)));

        string? reported = orchestrator.GetStatus().LastError;
        Assert.Contains("the job pipeline stopped", reported);
        Assert.Contains("channel is gone", reported);   // the underlying cause is not swallowed

        await orchestrator.StopAsync();
    }

    /// <summary>A stop/start cycle is the recovery, so it must clear the fault — otherwise a restarted
    /// engine would look permanently broken.</summary>
    [Fact]
    public async Task Restarting_clears_a_reported_pipeline_fault()
    {
        ThrowingTriggerQueue queue = new();
        JobOrchestrator orchestrator = OrchestratorOver(_profile, _executor, queue);

        Assert.True(orchestrator.Start().IsSuccess);
        Assert.True(await WaitForAsync(() => orchestrator.GetStatus().LastError is not null, TimeSpan.FromSeconds(5)));
        await orchestrator.StopAsync();

        queue.Healthy = true;
        Assert.True(orchestrator.Start().IsSuccess);
        Assert.Null(orchestrator.GetStatus().LastError);
        await orchestrator.StopAsync();
    }

    /// <summary>Throws out of the dequeue loop, the way a state-file or channel fault would. Set
    /// <see cref="Healthy"/> to make it behave like an idle queue instead.</summary>
    private sealed class ThrowingTriggerQueue : ITriggerQueue
    {
        public bool Healthy { get; set; }

        public int PendingCount => 0;

        public void Enqueue(Payload payload) { }

        public async IAsyncEnumerable<Payload> DequeueAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Healthy)
            {
                await Task.Delay(Timeout.Infinite, ct);   // an idle queue: never yields, honours cancel
                yield break;
            }
            await Task.Yield();
            throw new InvalidOperationException("the trigger channel is gone");
        }
    }

    [Fact]
    public async Task Forwards_residual_paths_onto_the_failed_event()
    {
        // The one outcome where the user MUST act, so the remediation list has to reach the wire.
        string[] residuals = [@"C:\left\behind.tmp"];
        _executor.OnExecute = plan => new JobCompletion(
            plan.JobId, JobOutcome.RollbackFailed, null,
            new JobError { Code = JobErrorCode.RollbackIncomplete, Message = "rollback failed" },
            TimeSpan.Zero) { ResidualPaths = residuals };

        Assert.True(_orchestrator.Start().IsSuccess);
        _queue.Enqueue(PayloadFor(_profile.Id));

        Assert.True(await WaitForAsync(
            () => { lock (_events) return _events.Any(e => e is JobFailedEvent); }, TimeSpan.FromSeconds(5)));
        lock (_events)
        {
            JobFailedEvent failed = _events.OfType<JobFailedEvent>().Single();
            Assert.Equal(residuals, failed.ResidualPaths);
        }
        await _orchestrator.StopAsync();
    }

    [Fact]
    public async Task Last_error_is_cleared_by_a_later_successful_job()
    {
        // LastError drives the status bar's health hint; one transient failure must not paint the
        // engine permanently unhealthy.
        _executor.OnExecute = plan => new JobCompletion(
            plan.JobId, JobOutcome.Failed, null,
            new JobError { Code = JobErrorCode.PlacementFailed, Message = "boom" }, TimeSpan.Zero);

        Assert.True(_orchestrator.Start().IsSuccess);
        _queue.Enqueue(PayloadFor(_profile.Id));
        Assert.True(await WaitForAsync(() => _orchestrator.GetStatus().LastError == "boom", TimeSpan.FromSeconds(5)));

        _executor.OnExecute = plan => new JobCompletion(plan.JobId, JobOutcome.Succeeded, null, null, TimeSpan.Zero);
        File.WriteAllText(_sourceFile, "again");
        _queue.Enqueue(PayloadFor(_profile.Id) with { SourcePath = _sourceFile });
        Assert.True(await WaitForAsync(() => _orchestrator.GetStatus().LastError is null, TimeSpan.FromSeconds(5)));

        await _orchestrator.StopAsync();
    }

    [Fact]
    public async Task Progress_samples_reach_the_bus_after_the_started_event()
    {
        _executor.OnProgress = progress => progress?.Report(new JobProgress(JobPhase.Distributing, 1, 1));

        Assert.True(_orchestrator.Start().IsSuccess);
        _queue.Enqueue(PayloadFor(_profile.Id));

        Assert.True(await WaitForAsync(
            () => { lock (_events) return _events.Any(e => e is JobProgressEvent); }, TimeSpan.FromSeconds(5)));
        lock (_events)
        {
            int startedAt = _events.FindIndex(e => e is JobStartedEvent);
            int progressAt = _events.FindIndex(e => e is JobProgressEvent);
            Assert.True(startedAt >= 0 && progressAt > startedAt,
                "a progress frame must never precede its own job-started");
            JobProgressEvent progress = _events.OfType<JobProgressEvent>().First();
            Assert.Equal(_executor.Executed.Single().JobId.Value, progress.JobId);
        }
        await _orchestrator.StopAsync();
    }

    private JobOrchestrator OrchestratorOver(Profile profile, FakeJobExecutor executor, ITriggerQueue queue)
    {
        EnginePaths paths = new() { Root = Path.Combine(_root, "engine-" + Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(paths.JobLogsDirectory);
        EngineConfig config = new();
        return new JobOrchestrator(
            queue, new FakeProfileCatalog(profile), executor, new JobPlanFactory(paths, config), _bus,
            new JobLogStore(paths, TimeProvider.System, NullLogger<JobLogStore>.Instance),
            new FakePauseState(), config, TimeProvider.System, NullLogger<JobOrchestrator>.Instance);
    }

    [Fact]
    public void GetStatus_reports_active_profiles_and_pause()
    {
        EngineStatusSnapshot status = _orchestrator.GetStatus();
        Assert.Equal(1, status.ActiveProfiles);
        Assert.False(status.Paused);
        Assert.Equal(0, status.JobsInFlight);
    }

    [Fact]
    public async Task StopAsync_drains_an_in_flight_job()
    {
        using ManualResetEventSlim gate = new(false);
        _executor.OnExecute = plan =>
        {
            gate.Wait(TimeSpan.FromSeconds(5));
            return new JobCompletion(plan.JobId, JobOutcome.Succeeded, null, null, TimeSpan.Zero);
        };

        Assert.True(_orchestrator.Start().IsSuccess);
        _queue.Enqueue(PayloadFor(_profile.Id));
        Assert.True(await WaitForAsync(() => _executor.Executed.Count == 1, TimeSpan.FromSeconds(5)));

        Task stop = _orchestrator.StopAsync();
        await Task.Delay(200);
        Assert.False(stop.IsCompleted, "StopAsync must await the in-flight job (I-ATOMIC-JOB)");

        gate.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _eventSub.Dispose();
        _queue.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
