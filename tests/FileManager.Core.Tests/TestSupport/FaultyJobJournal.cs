using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Wraps a real <see cref="JobJournal"/> and fails the append of a chosen record type.
/// <para>This is the deterministic way to force a failure at a specific point in the §7.2 write-ahead
/// sequence — in particular <em>after</em> a target has been placed, which no other injection point
/// reaches (metadata and verification both fail before the replace). Failing the
/// <c>job-committed</c> append is how the "forced failure at each lifecycle phase" criterion reaches
/// the rollback-restores-the-replaced-file path.</para></summary>
internal sealed class FaultyJobJournal(IJobJournal inner) : IJobJournal
{
    /// <summary>Appends of this record type fail. Null disables injection.</summary>
    public Type? FailOnRecordType { get; set; }

    public int FailedAppends { get; private set; }

    public Result Append(JournalRecord record)
    {
        if (FailOnRecordType is not null && record.GetType() == FailOnRecordType)
        {
            FailedAppends++;
            return Result.Failure($"injected journal failure for {record.GetType().Name}");
        }
        return inner.Append(record);
    }

    public Result<IReadOnlyList<JournalRecord>, JobError> ReadAll() => inner.ReadAll();

    public Result Rotate() => inner.Rotate();
}
