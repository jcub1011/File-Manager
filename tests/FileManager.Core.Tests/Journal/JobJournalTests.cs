using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Journal;

public sealed class JobJournalTests : IDisposable
{
    private readonly string _root;
    private readonly EnginePaths _paths;

    public JobJournalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-journal-" + Guid.NewGuid().ToString("N"));
        _paths = new EnginePaths { Root = _root };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JobJournal New(long rotateAt = 4L * 1024 * 1024) =>
        new(_paths, new EngineConfig { JournalRotateAtBytes = rotateAt }, NullLogger<JobJournal>.Instance);

    private static JobOpenedRecord Opened(Guid jobId) => new()
    {
        JobId = jobId, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch,
        ProfileId = Guid.NewGuid(),
        Source = new SourceSnapshot { Path = @"C:\s\f.txt", SizeBytes = 3, LastWriteUtc = DateTimeOffset.UnixEpoch },
        Policies = new PolicySnapshot
        {
            Verification = VerificationMethod.Sha256, OverwriteHandling = OverwriteHandling.StageOverwrites,
            ConflictResolution = ConflictResolution.Overwrite, OnSuccess = OnSuccessAction.KeepSource,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        WorkspaceDir = @"C:\work",
        Targets = [new TargetPlan { TargetIndex = 0, TargetRoot = @"C:\t", ProspectiveFinalPath = @"C:\t\f.txt" }],
    };

    [Fact]
    public void Round_trips_records_and_stamps_monotonic_sequence()
    {
        Guid job = Guid.NewGuid();
        using (JobJournal journal = New())
        {
            Assert.True(journal.Append(Opened(job)).IsSuccess);
            Assert.True(journal.Append(new JobCommittedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch }).IsSuccess);
        }

        using JobJournal reader = New();
        Result<IReadOnlyList<JournalRecord>, JobError> read = reader.ReadAll();
        Assert.True(read.TryGetValue(out IReadOnlyList<JournalRecord>? records));
        Assert.Equal(2, records.Count);
        Assert.IsType<JobOpenedRecord>(records[0]);
        Assert.IsType<JobCommittedRecord>(records[1]);
        Assert.Equal(1, records[0].Seq);
        Assert.Equal(2, records[1].Seq);
    }

    [Fact]
    public void Drops_a_torn_tail_line_but_keeps_the_good_records()
    {
        Guid job = Guid.NewGuid();
        using (JobJournal journal = New())
            journal.Append(Opened(job));

        // Simulate a torn final write: partial bytes with no terminating newline.
        string segment = Directory.GetFiles(_paths.JournalDirectory, "journal-*.ndjsonl").Single();
        File.AppendAllText(segment, "J1 deadbeef {\"t\":\"commit\",\"JobId\":");   // truncated, no '\n'

        using JobJournal reader = New();
        Result<IReadOnlyList<JournalRecord>, JobError> read = reader.ReadAll();
        Assert.True(read.TryGetValue(out IReadOnlyList<JournalRecord>? records));
        Assert.Single(records);
        Assert.IsType<JobOpenedRecord>(records[0]);
    }

    [Fact]
    public void Rotation_copies_open_jobs_forward_and_drops_closed_ones()
    {
        Guid openJob = Guid.NewGuid();
        Guid closedJob = Guid.NewGuid();
        using JobJournal journal = New();
        journal.Append(Opened(openJob));
        journal.Append(Opened(closedJob));
        journal.Append(new JobClosedRecord { JobId = closedJob, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, Outcome = JobOutcome.Succeeded });

        Assert.True(journal.Rotate().IsSuccess);

        Result<IReadOnlyList<JournalRecord>, JobError> read = journal.ReadAll();
        read.TryGetValue(out IReadOnlyList<JournalRecord>? records);
        Assert.All(records!, r => Assert.Equal(openJob, r.JobId));   // only the OPEN job survives
        Assert.Single(Directory.GetFiles(_paths.JournalDirectory, "journal-*.ndjsonl"));
    }
}
