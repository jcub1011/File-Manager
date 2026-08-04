using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Pins the one property the rest of the executor silently depends on: every thread working
/// a job sees the SAME per-target progress objects.
/// <para><see cref="JobExecution.Targets"/> and <see cref="JobExecution.States"/> used to be built
/// lazily with <c>??=</c>, which is not atomic. <c>JobExecutor.DistributeAsync</c> fans out one task
/// per target and every one of them touches <c>Targets</c> for the first time at the same moment, so
/// several threads each built their own array and only the last write to the field survived. Every
/// target that mutated a discarded <see cref="TargetProgress"/> still read as
/// <see cref="TargetState.Pending"/> when rollback snapshotted it — so rollback did nothing for that
/// target, leaving its temp on disk and, for a target that had already been placed, this job's
/// content at the destination while the job reported "rolled back cleanly".</para></summary>
public sealed class JobExecutionTests : IDisposable
{
    private readonly string _root;

    public JobExecutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JobExecution NewExecution(int targetCount)
    {
        string source = Path.Combine(_root, "src.dat");
        if (!File.Exists(source))
            File.WriteAllText(source, "payload");
        string targetRoot = Path.Combine(_root, "target");
        string[] finals = [.. Enumerable.Range(0, targetCount).Select(i => Path.Combine(targetRoot, $"out{i}.dat"))];
        return JobFixtures.Execution(source, _root, finals, [.. finals.Select(_ => targetRoot)]);
    }

    /// <summary>Runs <paramref name="body"/> on <paramref name="threads"/> real threads released
    /// together, so every one of them reaches its first line at the same moment. Parallel.For ramps
    /// its workers up, which is enough to hide a first-access race behind the first worker winning.
    /// </summary>
    private static void RunTogether(int threads, Action<int> body)
    {
        using var start = new ManualResetEventSlim(false);
        var running = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            int index = i;
            running[i] = new Thread(() => { start.Wait(); body(index); }) { IsBackground = true };
            running[i].Start();
        }
        start.Set();
        foreach (Thread thread in running)
            thread.Join();
    }

    [Fact]
    public void Concurrent_first_access_to_Targets_hands_every_thread_the_same_objects()
    {
        const int targets = 8;
        // Repeated because a torn lazy init is a race: one attempt can win by luck.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JobExecution execution = NewExecution(targets);
            var views = new TargetProgress[targets][];

            RunTogether(targets, i => views[i] = [.. execution.Targets]);

            for (int i = 0; i < targets; i++)
                for (int t = 0; t < targets; t++)
                    Assert.Same(views[0][t], views[i][t]);
        }
    }

    [Fact]
    public void Per_target_progress_written_concurrently_is_visible_afterwards()
    {
        // The real failure: each target task writes its own TargetProgress, then the executor thread
        // reads all of them to build the rollback item list. Anything written to a discarded copy is
        // lost, and rollback then skips a target that has artifacts on disk.
        const int targets = 8;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JobExecution execution = NewExecution(targets);

            RunTogether(targets, i =>
            {
                TargetProgress tp = execution.Targets[i];
                tp.TempPath = $"temp-{i}";
                tp.State = TargetState.TempWriting;
            });

            for (int i = 0; i < targets; i++)
            {
                Assert.Equal(TargetState.TempWriting, execution.Targets[i].State);
                Assert.Equal($"temp-{i}", execution.Targets[i].TempPath);
            }
        }
    }

    [Fact]
    public void Concurrent_first_access_to_States_hands_every_thread_the_same_machine()
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JobExecution execution = NewExecution(2);
            var seen = new JobStateMachine[8];
            RunTogether(seen.Length, i => seen[i] = execution.States);
            foreach (JobStateMachine machine in seen)
                Assert.Same(seen[0], machine);
        }
    }
}
