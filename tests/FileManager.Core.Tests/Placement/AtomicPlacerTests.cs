using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Placement;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Placement;

public sealed class AtomicPlacerTests : IDisposable
{
    private readonly string _root;
    private readonly EnginePaths _paths;
    private readonly JobJournal _journal;
    private readonly AtomicPlacer _placer;

    public AtomicPlacerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-placer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new EnginePaths { Root = Path.Combine(_root, "engine") };
        var time = new FakeTimeProvider();
        _journal = new JobJournal(_paths, new EngineConfig(), NullLogger<JobJournal>.Instance);
        _placer = new AtomicPlacer(
            new FileHasher(NullLogger<FileHasher>.Instance),
            _journal,
            new SelfWriteSuppressionRegistry(time),
            new TransientRetryPolicy(time, NullLogger<TransientRetryPolicy>.Instance),
            new FakeMetadataPreserver(),
            new SourcePriorityRegistry(),
            time,
            NullLogger<AtomicPlacer>.Instance);
    }

    public void Dispose()
    {
        _journal.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private (JobExecution Execution, string TargetPath) Setup(string content, string targetName, PolicySnapshot? policy = null)
    {
        string source = Path.Combine(_root, "src.dat");
        File.WriteAllText(source, content);
        string targetRoot = Path.Combine(_root, "target");
        string finalPath = Path.Combine(targetRoot, targetName);
        JobExecution execution = JobFixtures.Execution(source, _root, [finalPath], [targetRoot], policy);
        return (execution, finalPath);
    }

    private PlacementRequest Request(JobExecution execution, string finalPath, bool finalExists) => new()
    {
        Execution = execution,
        TargetIndex = 0,
        Output = execution.Output!,
        FinalPath = finalPath,
        FinalExists = finalExists,
        OverwriteHandling = execution.Plan.Policies.OverwriteHandling,
        Verification = execution.Plan.Policies.Verification,
    };

    [Fact]
    public async Task Places_a_fresh_file_with_matching_bytes_and_journals_the_sequence()
    {
        (JobExecution execution, string finalPath) = Setup("hello world", "out.dat");

        Result<PlacementResult, JobError> result = await _placer.PlaceTargetAsync(Request(execution, finalPath, finalExists: false));

        Assert.True(result.TryGetValue(out PlacementResult? placement));
        Assert.Equal(TargetState.Placed, placement.FinalState);
        Assert.Equal("hello world", File.ReadAllText(finalPath));
        Assert.Equal(TargetState.Placed, execution.Targets[0].State);
        Assert.False(File.Exists(finalPath + ".fmtmp-" + execution.Plan.JobId.Short));   // temp renamed away

        var records = ReadJournal();
        Assert.Contains(records, r => r is TargetWriteBeginRecord);
        Assert.Contains(records, r => r is TargetVerifiedRecord);
        Assert.Contains(records, r => r is TargetPlacedRecord);
    }

    [Fact]
    public async Task StageOverwrites_preserves_the_prior_version_in_staging()
    {
        (JobExecution execution, string finalPath) = Setup("new content", "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.WriteAllText(finalPath, "PRIOR VERSION");

        Result<PlacementResult, JobError> result = await _placer.PlaceTargetAsync(Request(execution, finalPath, finalExists: true));

        Assert.True(result.TryGetValue(out PlacementResult? placement));
        Assert.Equal("new content", File.ReadAllText(finalPath));
        Assert.NotNull(placement.StagedPath);
        Assert.Equal("PRIOR VERSION", File.ReadAllText(placement.StagedPath!));   // prior version safe in staging
        Assert.Contains(ReadJournal(), r => r is TargetStagedRecord);
    }

    [Fact]
    public async Task DirectOverwrite_replaces_in_place_without_staging()
    {
        var policy = JobFixtures.Policy(overwrite: OverwriteHandling.DirectOverwrite);
        (JobExecution execution, string finalPath) = Setup("fresh", "out.dat", policy);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.WriteAllText(finalPath, "old");

        Result<PlacementResult, JobError> result = await _placer.PlaceTargetAsync(Request(execution, finalPath, finalExists: true));

        Assert.True(result.TryGetValue(out PlacementResult? placement));
        Assert.Equal("fresh", File.ReadAllText(finalPath));
        Assert.Null(placement.StagedPath);
        Assert.DoesNotContain(ReadJournal(), r => r is TargetStagedRecord);
    }

    [Fact]
    public async Task Unchanged_target_short_circuits_and_journals_target_unchanged()
    {
        (JobExecution execution, string finalPath) = Setup("identical", "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.WriteAllText(finalPath, "identical");   // same content as the sealed source

        Result<UnchangedCheckResult, JobError> result = await _placer.CheckUnchangedAsync(execution, 0, finalPath);

        Assert.True(result.TryGetValue(out UnchangedCheckResult verdict));
        Assert.Equal(UnchangedCheckResult.Unchanged, verdict);
        Assert.Equal(TargetState.SatisfiedUnchanged, execution.Targets[0].State);
        Assert.Contains(ReadJournal(), r => r is TargetUnchangedRecord);
    }

    [Fact]
    public async Task Different_existing_file_is_not_unchanged()
    {
        (JobExecution execution, string finalPath) = Setup("incoming", "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.WriteAllText(finalPath, "different");

        Result<UnchangedCheckResult, JobError> result = await _placer.CheckUnchangedAsync(execution, 0, finalPath);
        result.TryGetValue(out UnchangedCheckResult verdict);
        Assert.Equal(UnchangedCheckResult.ExistsDifferent, verdict);
    }

    private IReadOnlyList<JournalRecord> ReadJournal()
    {
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        return records!;
    }
}
