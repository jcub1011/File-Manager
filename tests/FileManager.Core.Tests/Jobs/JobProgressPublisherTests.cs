using FileManager.Contracts.IPC;
using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Pins the publisher's two jobs: throttle the frame rate, and never let a caller see
/// progress go backwards (distribution reports from parallel target tasks).</summary>
public sealed class JobProgressPublisherTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static (JobProgressPublisher Publisher, RecordingEventBus Bus) New(TimeSpan interval)
    {
        RecordingEventBus bus = new();
        return (new JobProgressPublisher(JobId, bus, TimeProvider.System) { Interval = interval }, bus);
    }

    [Fact]
    public void The_first_sample_always_publishes()
    {
        // A job shorter than one interval must still emit exactly one frame, so the UI sees it move.
        (JobProgressPublisher publisher, RecordingEventBus bus) = New(TimeSpan.FromHours(1));

        publisher.Report(new JobProgress(JobPhase.Locking, 0, 2));

        JobProgressEvent evt = Assert.IsType<JobProgressEvent>(Assert.Single(bus.Events));
        Assert.Equal(JobId, evt.JobId);
        Assert.Equal(JobPhase.Locking, evt.Phase);
        Assert.Equal(2, evt.TargetCount);
    }

    [Fact]
    public void A_second_sample_inside_the_interval_is_suppressed()
    {
        (JobProgressPublisher publisher, RecordingEventBus bus) = New(TimeSpan.FromHours(1));

        publisher.Report(new JobProgress(JobPhase.Distributing, 0, 3));
        publisher.Report(new JobProgress(JobPhase.Distributing, 1, 3));

        Assert.Single(bus.Events);
    }

    [Fact]
    public void A_sample_after_the_interval_publishes_the_freshest_counts()
    {
        // Zero interval: nothing is ever throttled, so the suppressed-state carry-forward is visible.
        (JobProgressPublisher publisher, RecordingEventBus bus) = New(TimeSpan.Zero);

        publisher.Report(new JobProgress(JobPhase.Distributing, 1, 3));
        publisher.Report(new JobProgress(JobPhase.Distributing, 2, 3));

        Assert.Equal(2, bus.Events.Count);
        Assert.Equal(2, ((JobProgressEvent)bus.Events[1]).TargetsCompleted);
    }

    [Fact]
    public void A_backwards_phase_is_dropped()
    {
        (JobProgressPublisher publisher, RecordingEventBus bus) = New(TimeSpan.Zero);

        publisher.Report(new JobProgress(JobPhase.Committing, 2, 2));
        publisher.Report(new JobProgress(JobPhase.Distributing, 2, 2));   // stale, out of order

        Assert.Equal(JobPhase.Committing, ((JobProgressEvent)Assert.Single(bus.Events)).Phase);
    }

    [Fact]
    public void Target_counts_never_decrease()
    {
        (JobProgressPublisher publisher, RecordingEventBus bus) = New(TimeSpan.Zero);

        publisher.Report(new JobProgress(JobPhase.Distributing, 2, 3));
        publisher.Report(new JobProgress(JobPhase.Distributing, 1, 3));   // out-of-order sibling

        Assert.Equal(2, bus.Events.Count);
        Assert.Equal(2, ((JobProgressEvent)bus.Events[1]).TargetsCompleted);
    }

    [Fact]
    public async Task Concurrent_reports_publish_a_monotonic_sequence()
    {
        (JobProgressPublisher publisher, RecordingEventBus bus) = New(TimeSpan.Zero);
        const int targets = 64;

        await Parallel.ForAsync(1, targets + 1, (i, _) =>
        {
            publisher.Report(new JobProgress(JobPhase.Distributing, i, targets));
            return ValueTask.CompletedTask;
        });

        int previous = 0;
        foreach (EngineEvent evt in bus.Events)
        {
            int completed = ((JobProgressEvent)evt).TargetsCompleted;
            Assert.True(completed >= previous, $"progress went backwards: {previous} → {completed}");
            previous = completed;
        }
        Assert.Equal(targets, previous);
    }
}
