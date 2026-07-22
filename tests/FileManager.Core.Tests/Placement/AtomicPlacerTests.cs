using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Placement;
using FileManager.Core.Platform;
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

    [Fact]
    public async Task Read_back_mismatch_returns_VerificationMismatch_and_does_not_place_the_final()
    {
        (JobExecution execution, string finalPath) = Setup("hello world", "out.dat");

        // Tamper the sealed content hash: the temp is copied faithfully from the source, so its
        // read-back hash cannot match this bogus reference — forcing a VerificationMismatch.
        var tampered = new SealedOutput
        {
            Path = execution.Output!.Path,
            SizeBytes = execution.Output.SizeBytes,
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            SourceLastWriteUtc = execution.Output.SourceLastWriteUtc,
        };
        var request = new PlacementRequest
        {
            Execution = execution,
            TargetIndex = 0,
            Output = tampered,
            FinalPath = finalPath,
            FinalExists = false,
            OverwriteHandling = execution.Plan.Policies.OverwriteHandling,
            Verification = execution.Plan.Policies.Verification,   // Sha256 → content is hashed
        };

        Result<PlacementResult, JobError> result = await _placer.PlaceTargetAsync(request);

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.VerificationMismatch, error.Code);
        Assert.False(File.Exists(finalPath));   // the final was never placed
    }

    [Fact]
    public async Task StageOverwrites_journals_exactly_one_target_staged_row_for_the_job()
    {
        (JobExecution execution, string finalPath) = Setup("new content", "out.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.WriteAllText(finalPath, "PRIOR VERSION");

        Result<PlacementResult, JobError> result = await _placer.PlaceTargetAsync(Request(execution, finalPath, finalExists: true));

        Assert.True(result.TryGetValue(out PlacementResult? placement));

        Guid jobId = execution.Plan.JobId.Value;
        int stagedRows = ReadJournal().OfType<TargetStagedRecord>().Count(r => r.JobId == jobId);
        Assert.Equal(1, stagedRows);   // journaled once, before the retried replace — never duplicated

        Assert.NotNull(placement.StagedPath);
        Assert.Equal("PRIOR VERSION", File.ReadAllText(placement.StagedPath!));   // prior file at staged path
        Assert.Equal("new content", File.ReadAllText(finalPath));                 // new content at final
    }

    [Fact]
    public async Task MetadataApply_failure_returns_MetadataConflict()
    {
        // Under MetadataOnConflict.FailJob a preserver failure must fail the placement. The fake
        // preserver fails unconditionally, so the placer maps it to a MetadataConflict JobError.
        PolicySnapshot policy = JobFixtures.Policy() with { MetadataOnConflict = MetadataOnConflict.FailJob };
        (JobExecution execution, string finalPath) = Setup("payload", "meta.dat", policy);
        AtomicPlacer placer = PlacerWith(new FakeMetadataPreserver { FailApply = true });

        Result<PlacementResult, JobError> result = await placer.PlaceTargetAsync(Request(execution, finalPath, finalExists: false));

        Assert.True(result.TryGetError(out JobError? error));
        Assert.Equal(JobErrorCode.MetadataConflict, error.Code);
        Assert.False(File.Exists(finalPath));   // metadata is applied before the rename, so no final
    }

    [Fact]
    public async Task Fresh_place_into_a_new_directory_moves_temp_to_final()
    {
        // A plain fresh place (FinalExists=false) into a not-yet-existing target directory exercises
        // the PlaceAsync fresh-file branch (File.Move without overwrite).
        (JobExecution execution, string finalPath) = Setup("brand new", "nested.dat");
        string tempPath = finalPath + ".fmtmp-" + execution.Plan.JobId.Short;

        Result<PlacementResult, JobError> result = await _placer.PlaceTargetAsync(Request(execution, finalPath, finalExists: false));

        Assert.True(result.TryGetValue(out PlacementResult? placement));
        Assert.Equal(TargetState.Placed, placement.FinalState);
        Assert.Null(placement.StagedPath);
        Assert.Equal("brand new", File.ReadAllText(finalPath));
        Assert.False(File.Exists(tempPath));   // temp moved away, not left behind
    }

    /// <summary>Builds a placer sharing this fixture's journal but with a custom metadata preserver.</summary>
    private AtomicPlacer PlacerWith(IMetadataPreserver metadata)
    {
        var time = new FakeTimeProvider();
        return new AtomicPlacer(
            new FileHasher(NullLogger<FileHasher>.Instance),
            _journal,
            new SelfWriteSuppressionRegistry(time),
            new TransientRetryPolicy(time, NullLogger<TransientRetryPolicy>.Instance),
            metadata,
            new SourcePriorityRegistry(),
            time,
            NullLogger<AtomicPlacer>.Instance);
    }

    private IReadOnlyList<JournalRecord> ReadJournal()
    {
        _journal.ReadAll().TryGetValue(out IReadOnlyList<JournalRecord>? records);
        return records!;
    }
}
