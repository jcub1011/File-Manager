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

        await Task.Delay(300);
        Assert.Empty(_executor.Executed);
        await _orchestrator.StopAsync();
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
