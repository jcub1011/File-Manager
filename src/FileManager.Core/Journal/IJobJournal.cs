using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System.Collections.Generic;

namespace FileManager.Core.Journal;

public interface IJobJournal
{
    Result Append(JournalRecord record);

    /// <summary>All segments oldest-first; drops a torn tail line (§5.5). Recovery only.</summary>
    Result<IReadOnlyList<JournalRecord>, JobError> ReadAll();

    /// <summary>Copies OPEN-job records forward into a fresh segment, then deletes old segments (I-APPEND).</summary>
    Result Rotate();
}
