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

    [Fact]
    public void Size_triggered_auto_rotation_compacts_and_keeps_open_job_records_once()
    {
        Guid openJob = Guid.NewGuid();
        Guid closedJob = Guid.NewGuid();
        // Tiny threshold: every durable append trips the auto-rotation path.
        using JobJournal journal = New(rotateAt: 64);
        journal.Append(Opened(openJob));
        journal.Append(Opened(closedJob));
        journal.Append(new JobClosedRecord { JobId = closedJob, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, Outcome = JobOutcome.Succeeded });
        journal.Append(new JobCommittedRecord { JobId = openJob, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch });

        Result<IReadOnlyList<JournalRecord>, JobError> read = journal.ReadAll();
        Assert.True(read.TryGetValue(out IReadOnlyList<JournalRecord>? records));
        // The open job's records survive every rotation exactly once; the closed job is compacted away.
        Assert.All(records!, r => Assert.Equal(openJob, r.JobId));
        Assert.Equal(2, records!.Count(r => r.JobId == openJob));   // Opened + Committed, no duplicates
        Assert.Contains(records!, r => r is JobOpenedRecord);
        Assert.Contains(records!, r => r is JobCommittedRecord);
        Assert.Single(Directory.GetFiles(_paths.JournalDirectory, "journal-*.ndjsonl"));   // compacted to one segment
    }

    [Fact]
    public void Mid_segment_crc_corruption_is_skipped_while_good_records_still_load()
    {
        Guid job = Guid.NewGuid();
        using (JobJournal journal = New())
        {
            journal.Append(Opened(job));                                                                            // line 0 — we corrupt this (non-tail)
            journal.Append(new JobCommittedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch });        // line 1
            journal.Append(new JobClosedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch, Outcome = JobOutcome.Succeeded }); // line 2
        }

        string segment = Directory.GetFiles(_paths.JournalDirectory, "journal-*.ndjsonl").Single();
        byte[] bytes = File.ReadAllBytes(segment);
        int firstNewline = Array.IndexOf(bytes, (byte)'\n');
        Assert.True(firstNewline > 21);   // line 0 is longer than the "J1 <8hex> " frame header
        bytes[20] ^= 0xFF;                // corrupt a payload byte in the first (non-tail) line => CRC mismatch
        File.WriteAllBytes(segment, bytes);

        using JobJournal reader = New();
        Result<IReadOnlyList<JournalRecord>, JobError> read = reader.ReadAll();
        Assert.True(read.TryGetValue(out IReadOnlyList<JournalRecord>? records));
        Assert.Equal(2, records!.Count);                              // the corrupt line is logged-and-skipped
        Assert.DoesNotContain(records!, r => r is JobOpenedRecord);   // the corrupted record did not load
        Assert.Contains(records!, r => r is JobCommittedRecord);
        Assert.Contains(records!, r => r is JobClosedRecord);
    }

    [Fact]
    public void Duplicate_records_across_segments_are_tolerated_on_read()
    {
        Guid job = Guid.NewGuid();
        using (JobJournal journal = New())
        {
            journal.Append(Opened(job));
            journal.Append(new JobCommittedRecord { JobId = job, Seq = 0, AtUtc = DateTimeOffset.UnixEpoch });
        }

        // Simulate a crash mid-rotation: the new segment was written but the old one was not yet
        // deleted, so both segments carry the same still-open-job records.
        string first = Directory.GetFiles(_paths.JournalDirectory, "journal-*.ndjsonl").Single();
        string second = Path.Combine(_paths.JournalDirectory, "journal-000002.ndjsonl");
        File.Copy(first, second);

        using JobJournal reader = New();
        Result<IReadOnlyList<JournalRecord>, JobError> read = reader.ReadAll();
        Assert.True(read.TryGetValue(out IReadOnlyList<JournalRecord>? records));   // no crash on duplicates
        Assert.Equal(4, records!.Count);                                            // duplicated across both segments
        Assert.Single(records!.Select(r => r.JobId).Distinct());                    // grouped by job => one job
        Assert.Equal(2, records!.Count(r => r is JobOpenedRecord));                 // duplicate open tolerated
    }
}
