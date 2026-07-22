using FileManager.Core.Jobs;
using FileManager.Core.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Observability;

public sealed class JobLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-joblog-" + Guid.NewGuid().ToString("N"));
    private readonly JobLogStore _store;

    public JobLogStoreTests()
    {
        EnginePaths paths = new() { Root = _root };
        Directory.CreateDirectory(paths.JobLogsDirectory);
        _store = new JobLogStore(paths, TimeProvider.System, NullLogger<JobLogStore>.Instance);
    }

    [Fact]
    public void Append_then_read_returns_the_lines()
    {
        Guid jobId = Guid.NewGuid();
        _store.Append(jobId, "opened");
        _store.Append(jobId, "placed target 0");

        Assert.True(_store.Read(jobId).TryGetValue(out IReadOnlyList<string>? lines));
        Assert.Equal(2, lines!.Count);
        Assert.Contains(lines, l => l.Contains("opened"));
        Assert.Contains(lines, l => l.Contains("placed target 0"));
    }

    [Fact]
    public void Reading_an_unknown_job_is_a_failure()
    {
        Assert.True(_store.Read(Guid.NewGuid()).TryGetError(out _));
    }

    [Fact]
    public void List_recent_returns_newest_first()
    {
        JobSummary older = Summary("older");
        JobSummary newer = Summary("newer");
        _store.RecordSummary(older);
        _store.RecordSummary(newer);

        Assert.True(_store.ListRecent(10).TryGetValue(out IReadOnlyList<JobSummary>? recent));
        Assert.Equal(2, recent!.Count);
        Assert.Equal("newer", recent[0].SourcePath);
        Assert.Equal("older", recent[1].SourcePath);
    }

    private static JobSummary Summary(string source) =>
        new(Guid.NewGuid(), Guid.NewGuid(), source, JobOutcome.Succeeded, null, DateTimeOffset.UnixEpoch, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
