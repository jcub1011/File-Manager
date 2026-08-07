using FileManager.Contracts.IPC;
using FileManager.Core.Observability;
using System;

namespace FileManager.Core.Jobs;

/// <summary>Turns one job's executor progress samples into throttled <see cref="JobProgressEvent"/>s
/// (§4.3, §4.10). One instance per job — it owns that job's last-published sample.
/// <para>Rate-limited to ~10 frames/sec, the same structural bound the streamed dry run uses, so a
/// profile with hundreds of targets cannot flood the event bus's synchronous fan-out or overrun a
/// subscriber's bounded 1024-frame channel. The first sample for a job always publishes, so the UI
/// sees a job move immediately and a job shorter than one interval still emits exactly one frame;
/// after that the phase the UI shows is at most one interval stale. The terminal
/// <c>job-completed</c>/<c>job-failed</c> event — never a progress frame — is authoritative.</para></summary>
public sealed class JobProgressPublisher(Guid jobId, IEngineEventBus bus, TimeProvider time)
    : IProgress<JobProgress>
{
    /// <summary>Minimum gap between published frames. Matches DryRunStreamHandler's ProgressInterval.
    /// Internal purely as a test seam (mirrors DryRunStreamHandler.RecycleWireRecords) — it is not
    /// user-configurable, and deliberately not in EngineConfig, which is not settings-bound.</summary>
    internal TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private bool _published;
    private long _lastPublishedAt;
    private JobPhase _lastPhase;
    private int _lastCompleted;

    public void Report(JobProgress value)
    {
        lock (_gate)
        {
            // Distribution is bounded-parallel, so samples can arrive out of order. Clamp both axes
            // monotonically: a phase that went backwards is stale and dropped outright, and the target
            // count only ever rises — otherwise the UI visibly counts backwards.
            if (_published && value.Phase < _lastPhase)
                return;
            int completed = _published ? Math.Max(_lastCompleted, value.TargetsCompleted) : value.TargetsCompleted;

            long now = time.GetTimestamp();
            if (_published && time.GetElapsedTime(_lastPublishedAt, now) < Interval)
            {
                // Suppressed by the throttle, but the clamp state still advances so the next frame
                // that does go out carries the freshest counts.
                _lastPhase = value.Phase;
                _lastCompleted = completed;
                return;
            }

            _published = true;
            _lastPublishedAt = now;
            _lastPhase = value.Phase;
            _lastCompleted = completed;

            // Publish INSIDE the lock. Clamping the counts is only half the guarantee: the frames also
            // have to reach the bus in the order they were clamped. Publishing outside meant a thread
            // could compute a frame, be descheduled before publishing, and have a later thread's higher
            // frame overtake it — so a subscriber saw exactly the backwards count this class exists to
            // prevent, even though every frame was individually clamped.
            //
            // Nesting the bus's lock inside this one is safe: SubscriberList takes its gate only to
            // snapshot the subscriber list (it fans out afterwards, unlocked), and nothing on that path
            // ever acquires this gate, so the two can never be taken in the opposite order. Cost is
            // negligible — one publisher per job, throttled to ~10 frames/sec.
            bus.Publish(new JobProgressEvent
            {
                AtUtc = time.GetUtcNow(),
                JobId = jobId,
                Phase = value.Phase,
                TargetsCompleted = completed,
                TargetCount = value.TargetCount,
            });
        }
    }
}
