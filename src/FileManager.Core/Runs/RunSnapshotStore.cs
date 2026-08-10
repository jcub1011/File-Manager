using FileManager.Contracts.DryRun;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FileManager.Core.Runs;

/// <summary>Writes one run's frozen work list as the plan streams in, so a run of any size never holds
/// its work list in memory.
///
/// <para><b>Layout</b> — <c>&lt;RunsDirectory&gt;/&lt;run-id&gt;/</c> holding <c>plan.json</c> (the
/// header, written last, once planning has produced its totals) plus <c>copies.ndjsonl</c>,
/// <c>deletes.ndjsonl</c>, and — for display only — <c>sources.ndjsonl</c> and
/// <c>destinations.ndjsonl</c>. Separate files rather than one with a
/// discriminator, so each reader touches only what it needs: the deletion pass parses the small delete
/// half without reading the copy half (the overwhelming majority of the bytes on a large run), execution
/// reads only copies, and the destination projection — which nothing in execution reads at all — stays
/// out of both their ways.</para>
///
/// <para><b>The two display files exist purely to be looked at</b>, and they hold different SETS from the
/// executable ones: <c>copies.ndjsonl</c> lists only files there is work for, while
/// <c>sources.ndjsonl</c> lists every file the plan LOOKED at. That difference is the whole reason they
/// are separate — an already-synchronized profile has no copies at all, so a view built from the copy list
/// shows an empty panel where the honest answer is "these files, every one already up to date". Together
/// they roughly double a snapshot's size, and the whole directory is deleted when the run closes.</para>
///
/// <para><b>Nothing bounds a snapshot's size but the volume it sits on</b>, since the plan lost its
/// file-count cap (a plan is executed, so a cap made a wrong work list rather than a small one). The
/// volume is therefore checked as the plan streams — see <see cref="CheckDiskEveryRows"/> — and running
/// out FAILS the run through the same <see cref="Failure"/> channel a write error uses. That direction
/// matters: a snapshot that stops short is an executable work list quietly missing entries.</para>
///
/// <para><b>Durability</b> is deliberately weaker than the journal's. Losing a snapshot means a run
/// must be planned again; it is not data loss, and the journal plus
/// <see cref="Audit.IReconcileAuditLog"/> remain the durable record of anything actually done. So the
/// item files are buffered and flushed once at completion rather than fsync'd per line — 500,000
/// fsyncs to describe work that has not happened yet would be absurd. The header IS flushed to disk,
/// because it is small, written once, and is what makes an abandoned run directory identifiable
/// afterwards.</para></summary>
internal sealed class RunSnapshotWriter : IDisposable
{
    /// <summary>Rows between free-space samples. <c>DriveInfo.AvailableFreeSpace</c> is a syscall, so it
    /// is not something to do per row; a large run writes a few hundred bytes a row, so 65,536 rows is
    /// tens of MB of overshoot at worst — well inside the reserve the check is measuring against.</summary>
    internal const int CheckDiskEveryRows = 65_536;

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly long _diskReserveBytes;
    private readonly DriveInfo? _volume;
    private long _rowsWritten;
    private long _nextDiskCheckAt = CheckDiskEveryRows;
    private FileStream? _copies;
    private FileStream? _deletes;
    private FileStream? _sources;
    private FileStream? _destinations;
    private string? _failure;

    // The paging sidecars, built as the display halves stream past. Both are best-effort: their absence
    // costs the preview a windowed read (it falls back to a scan) or its ordering (it falls back to plan
    // order), never correctness of the work list — which is why neither writes to _failure.
    private readonly RunSnapshotBlockIndex.Builder _sourceBlocks = new();
    private readonly RunSnapshotBlockIndex.Builder _destinationBlocks = new();
    private readonly RunSnapshotBlockIndex.Builder _deleteBlocks = new();
    private readonly RunSnapshotOrder.Builder _sourceOrder;
    private readonly RunSnapshotOrder.Builder _destinationOrder;

    public RunSnapshotWriter(string runDirectory, ILogger logger, long diskReserveBytes)
    {
        _directory = runDirectory;
        _logger = logger;
        _diskReserveBytes = diskReserveBytes;
        // The run's own directory doubles as the sort's scratch space: it is on the volume already sized
        // for the snapshot, and it is deleted wholesale when the run closes, so a crashed plan cannot
        // leave run files behind anywhere else.
        _sourceOrder = new RunSnapshotOrder.Builder(runDirectory, "src", logger);
        _destinationOrder = new RunSnapshotOrder.Builder(runDirectory, "dst", logger);
        try
        {
            Directory.CreateDirectory(runDirectory);
            _copies = Open(RunSnapshotPaths.CopiesFileName);
            _deletes = Open(RunSnapshotPaths.DeletesFileName);
            _sources = Open(RunSnapshotPaths.SourcesFileName);
            _destinations = Open(RunSnapshotPaths.DestinationsFileName);
            // Resolved once, here, rather than per check: the directory cannot move, and a run on a
            // path DriveInfo cannot parse (a UNC runs directory) should not fail — it just goes
            // unguarded, exactly as it was before the guard existed.
            _volume = ResolveVolume(runDirectory, logger);
        }
        catch (Exception ex)
        {
            // Recorded rather than thrown: the planner is mid-stream and a snapshot failure must
            // surface as a failed run, not as an exception unwinding an async iterator.
            _failure = $"could not create the run snapshot at \"{runDirectory}\": {ex.Message}";
            logger.LogError(ex, "Could not create the run snapshot at {Directory}", runDirectory);
        }

        FileStream Open(string name) => new(
            Path.Combine(runDirectory, name), FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024);
    }

    /// <summary>The volume the snapshot lands on, or null when it cannot be determined. Null means the
    /// space guard stands down rather than guesses — a UNC runs directory has no <see cref="DriveInfo"/>,
    /// and refusing to plan because we could not measure the disk would be worse than not measuring
    /// it.</summary>
    private static DriveInfo? ResolveVolume(string runDirectory, ILogger logger)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(runDirectory));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)
                ? null
                : new DriveInfo(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex,
                "Run snapshot: free space on {Directory} cannot be measured; the plan runs unguarded",
                runDirectory);
            return null;
        }
    }

    /// <summary>Fails the snapshot once the volume it is streaming onto drops below the reserve.
    /// Sampled every <see cref="CheckDiskEveryRows"/> rows; a no-op when the volume is unknown.
    ///
    /// <para>Records into <see cref="Failure"/> rather than throwing, for the same reason the
    /// constructor does: the planner is mid-iterator and this has to come back as a failed run.
    /// <see cref="Consume"/> then short-circuits on every subsequent chunk, and
    /// <c>RunCoordinator.PlanAsync</c> stops the walk instead of scanning a tree it can no longer
    /// record.</para></summary>
    private void CheckDiskSpace()
    {
        if (_volume is null || _rowsWritten < _nextDiskCheckAt)
            return;
        _nextDiskCheckAt = _rowsWritten + CheckDiskEveryRows;

        long free;
        try
        {
            free = _volume.AvailableFreeSpace;
        }
        catch (IOException ex)
        {
            // The volume went away mid-plan (a removed drive). Stop measuring rather than fail on it —
            // the next actual write is what will report the real problem, with a better message.
            _logger.LogDebug(ex, "Run snapshot: free space could not be read; the plan runs unguarded");
            return;
        }
        if (free >= _diskReserveBytes)
            return;

        _failure =
            $"the volume holding the run snapshot is nearly full: {free >> 20:N0} MB free, below the " +
            $"{_diskReserveBytes >> 20:N0} MB reserve, after {_rowsWritten:N0} planned rows. " +
            "Free space on that volume, or point the runs directory at a larger one.";
        _logger.LogError(
            "Run snapshot at {Directory} stopped: {FreeMb} MB free is below the {ReserveMb} MB reserve " +
            "after {Rows} rows",
            _directory, free >> 20, _diskReserveBytes >> 20, _rowsWritten);
    }

    public int CopyCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int SourceCount { get; private set; }
    public int DestinationCount { get; private set; }
    public long CopyBytes { get; private set; }
    public long DeleteBytes { get; private set; }

    /// <summary>The blast-radius counts: destination files this plan will overwrite, destinations it will
    /// write under a suffixed name because something was already there, and sources it will move or
    /// delete after copying.
    ///
    /// <para><b>Recorded rather than left to the client, for the same reason as
    /// <see cref="SweptByTargetRoot"/>: this is the only pass that sees every operation.</b> The Preview
    /// tab folds these three itself while it streams the plan (<c>DryRunRowStore.Complete</c>), which is
    /// fine for a view that has the rows anyway — but the job queue wants the same three numbers beside a
    /// selected run and must not replay a 500,000-row plan to get them. Counted here they cost three
    /// increments during a walk that is already happening, and the header read that returns them is
    /// O(1).</para>
    ///
    /// <para>The rules match <c>DryRunRowStore</c>'s exactly, deliberately: two surfaces showing the same
    /// blast radius from different sources must not disagree about it.</para></summary>
    public int OverwriteCount { get; private set; }

    public int RenameCount { get; private set; }

    public int DisposalCount { get; private set; }

    /// <summary>Every pre-existing file the destination sweep classified, tallied by the target root it
    /// sits under — survivors and orphans together.
    /// <para>This is the denominator the Mirror deletion pass's ratio guard needs, and the only place it
    /// can be counted honestly: the sweep sees each destination file exactly once, here. Reconstructing
    /// it downstream from the copy items cannot work, because an already-identical file is
    /// <see cref="OperationKind.SkippedUnchanged"/> and produces no copy item at all — so a
    /// synchronized profile's denominator collapsed to its own orphan count and the guard refused every
    /// steady-state pass at 100%.</para></summary>
    public IReadOnlyDictionary<string, int> SweptByTargetRoot => _sweptByTargetRoot;

    /// <summary>The ratio guard's NUMERATOR: orphans tallied by the target root they sit under, a
    /// subset of <see cref="SweptByTargetRoot"/>'s population.
    /// <para>Counted here for a reason beyond symmetry. The guard used to derive this itself by
    /// iterating the deletion list, which meant the caller had to materialize every orphan before the
    /// guard could decide anything — the one snapshot consumer that did not stream, and unbounded once
    /// a plan lost its file cap. Tallied during this walk it is one dictionary increment per orphan,
    /// and the deletion pass reads it from the header in O(1).</para></summary>
    public IReadOnlyDictionary<string, int> OrphansByTargetRoot => _orphansByTargetRoot;

    /// <summary>The Preview tab's facet and status aggregates, folded over the whole plan. See
    /// <see cref="RunSnapshotHeader.SourceRowsByRoot"/> for why the header carries them: a windowed
    /// client holds a page at a time and cannot compute a whole-plan total for itself.</summary>
    public IReadOnlyDictionary<string, int> SourceRowsByRoot => _sourceRowsByRoot;

    public IReadOnlyDictionary<string, int> DestinationRowsByRoot => _destinationRowsByRoot;

    public int UntouchedCount { get; private set; }

    public int ProcessedCount { get; private set; }

    /// <summary>Destination operations by kind, orphans included. See
    /// <see cref="RunSnapshotHeader.DestinationRowsByKind"/> for why this is by kind and not by the
    /// Destinations tab's chip names.</summary>
    public IReadOnlyDictionary<OperationKind, int> DestinationRowsByKind => _destinationRowsByKind;

    private readonly Dictionary<string, int> _sweptByTargetRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _orphansByTargetRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sourceRowsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _destinationRowsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<OperationKind, int> _destinationRowsByKind = [];

    private static void Bump(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private void CountSwept(string targetRoot) =>
        _sweptByTargetRoot[targetRoot] = _sweptByTargetRoot.GetValueOrDefault(targetRoot) + 1;

    private void CountOrphan(string targetRoot) =>
        _orphansByTargetRoot[targetRoot] = _orphansByTargetRoot.GetValueOrDefault(targetRoot) + 1;

    /// <summary>Whether a destination row describes a file that was ALREADY on disk before this run —
    /// which is what the ratio guard is measuring against.
    /// <para><c>New</c> and <c>Rename</c> name paths that do not exist yet, so they are not part of the
    /// population a deletion could remove a proportion of. Everything else on the destination side
    /// implies a pre-existing file: something to overwrite, something that forced a conflict, something
    /// already identical, a sweep find with no source, an orphan, or an entry the sweep could not
    /// classify. Excluding <c>Rename</c> also under-counts by the file that forced the suffix, which is
    /// the safe direction — an under-estimate can only make the guard stricter.</para></summary>
    private static bool DescribesExistingFile(OperationKind kind) =>
        kind is not (OperationKind.New or OperationKind.Rename);

    /// <summary>The first write failure, or null. Non-null means the snapshot is unusable and the run
    /// must fail rather than execute a partial work list — executing a truncated copy list is merely
    /// incomplete, but executing a truncated DELETE list against a complete survivor set is not a
    /// thing that can be allowed to happen quietly.</summary>
    public string? Failure => _failure;

    /// <summary>Translates one planned chunk into work items.
    ///
    /// <para><b>Copy items come only from <see cref="OperationKind.Processed"/> source operations.</b>
    /// A file the filters excluded (<see cref="OperationKind.SkippedByFilter"/>) and a file already
    /// identical at every target (<see cref="OperationKind.SkippedUnchanged"/>) both produce NO work,
    /// which is exactly what the preview showed for them. That is the point of executing from a
    /// snapshot: the plan decides, so a file that changes between planning and execution is picked up
    /// by the NEXT run rather than silently altering the run the user approved. Note it is safe under
    /// Mirror specifically because an unchanged file still contributes a destination operation, and
    /// therefore a survivor — so declining to re-copy it can never make its destination look
    /// orphaned.</para>
    ///
    /// <para><b>Delete items come from <see cref="OperationKind.Deleted"/> destination operations</b>,
    /// each paired to the file it names through its global <c>SubjectIndex</c> less the chunk's
    /// destination base. A reparse-point orphan is classified <see cref="OperationKind.Unknown"/> by
    /// the sweep, never <c>Deleted</c>, so it is excluded here by construction rather than by a
    /// special case.</para>
    ///
    /// <para><b>The two display halves are written from wider sets.</b> Every scanned source becomes a
    /// <see cref="RunSourceItem"/> whatever the plan decided about it, and every non-<c>Deleted</c>
    /// destination operation becomes a <see cref="RunDestinationItem"/> keyed to the source whose content
    /// lands there. Nothing in execution reads either; see their records for why they exist.</para></summary>
    public void Consume(PlanChunk chunk, Profile profile)
    {
        if (_failure is not null || chunk.PhaseStarted)
            return;

        DryRunChunk slice = chunk.Chunk;

        // What each of this chunk's sources does at its targets, folded BEFORE the sources are written
        // because that is when the answer has to be on hand. The Sources tab shows one rolled-up glyph per
        // row for this, and used to derive it by walking the row's destination operations — which a
        // windowed client cannot do, since a page of sources.ndjsonl never sees the destination half.
        //
        // op.SourceIndex is the plan's GLOBAL source ordinal (see WriteDestination), so it resolves against
        // this chunk by subtracting SourceBase. An op naming a source outside this chunk is skipped rather
        // than deferred: it would need an unbounded pending map, and the cost of missing one is a row with
        // no target glyph — which is exactly what a pre-existing snapshot renders as anyway. Orphans carry
        // -1 and contribute nothing, correctly: nothing sources them.
        int[]? targetKinds = null;
        foreach (IFileOperationView op in slice.DestinationOperations)
        {
            int local = op.SourceIndex - chunk.SourceBase;
            if ((uint)local >= (uint)slice.SourceFiles.Count)
                continue;
            (targetKinds ??= new int[slice.SourceFiles.Count])[local] |= OperationKindMask.Bit(op.Kind);
        }

        // The display source list: EVERY scanned file, whatever the plan decided about it, in plan order —
        // so an item's ordinal is the source index the plan assigned it and a destination row can name it.
        // Driven off the files rather than the operations because the files are the row set: a file whose
        // operation went missing must still appear, or it silently vanishes from the view.
        for (int i = 0; i < slice.SourceFiles.Count; i++)
        {
            IPhysicalFileView file = slice.SourceFiles[i];
            IFileOperationView? op = i < slice.SourceOperations.Count ? slice.SourceOperations[i] : null;
            // Before the write, so the offset is where this row STARTS.
            _sourceBlocks.Row(_sources?.Position ?? 0);
            _sourceOrder.Row(file.Path, file.Root);
            Write(_sources, RunSnapshotJsonContext.Default.RunSourceItem, new RunSourceItem
            {
                Path = file.Path,
                SourceRoot = file.Root,
                SizeBytes = file.Length,
                LastWriteUtc = file.LastWritten,
                Kind = op?.Kind ?? OperationKind.Unknown,
                Disposition = op?.SourceDisposition,
                Detail = op?.Detail,
                TargetKinds = targetKinds is null ? 0 : targetKinds[i],
            });
            SourceCount++;
            // The Preview tab's facet and status counts, under the SAME rules DryRunRowStore applied
            // when it folded them client-side (a file with no operation reads as Processed). Two
            // surfaces showing the same totals from different sources must not disagree.
            Bump(_sourceRowsByRoot, file.Root);
            switch (op?.Kind ?? OperationKind.Processed)
            {
                case OperationKind.SkippedByFilter or OperationKind.SkippedUnchanged: UntouchedCount++; break;
                case OperationKind.Processed: ProcessedCount++; break;
                default: break;
            }
            // A disposal that is DESTRUCTIVE — the original does not survive the run. KeepSource is the
            // only non-destructive action, and a null disposition means the file does not process at all
            // (filtered, or already identical), which is what keeps a skipped file out of this count.
            // Same test as DryRunRowStore.IsSourceDisposalDestructive.
            if (op?.SourceDisposition is { } disposition && disposition != OnSuccessAction.KeepSource)
                DisposalCount++;
        }

        for (int i = 0; i < slice.SourceOperations.Count; i++)
        {
            IFileOperationView op = slice.SourceOperations[i];
            if (op.Kind != OperationKind.Processed)
                continue;
            int local = op.SubjectIndex >= 0 ? op.SubjectIndex - chunk.SourceBase : i;
            if (local < 0 || local >= slice.SourceFiles.Count)
                continue;
            IPhysicalFileView file = slice.SourceFiles[local];
            Write(_copies, RunSnapshotJsonContext.Default.RunCopyItem, new RunCopyItem
            {
                SourcePath = file.Path,
                SourceRoot = file.Root,
                SizeBytes = file.Length,
                LastWriteUtc = file.LastWritten,
                PlannedKind = OperationKind.Processed,
                // Resolved here, through the SAME helper the live plan factory uses, so the recorded
                // rank and the rank the conflict resolver later compares against cannot disagree.
                SourceIndex = JobPlanFactory.ResolveSourceIndex(profile, file.Root),
            });
            CopyCount++;
            CopyBytes += file.Length;
        }

        foreach (IFileOperationView op in slice.DestinationOperations)
        {
            // Counted over EVERY destination operation, including any the projection writer below drops
            // for an unresolvable index — the blast radius is what the plan decided, not what the display
            // half managed to record. This is also what DryRunRowStore does (its fold runs over every
            // ingested operation, grouped or not), which is what keeps the two in agreement.
            switch (op.Kind)
            {
                case OperationKind.Overwrite: OverwriteCount++; break;
                case OperationKind.Rename: RenameCount++; break;
                default: break;
            }
            // The Destinations tab's status chips, over the whole plan and by KIND — the tab folds these
            // into its four chips itself. Counted over every operation for the same reason the two above
            // are: the blast radius is what the plan decided, not what the display half managed to record.
            _destinationRowsByKind[op.Kind] = _destinationRowsByKind.GetValueOrDefault(op.Kind) + 1;

            if (op.Kind != OperationKind.Deleted)
            {
                WriteDestination(op, chunk, slice);
                continue;
            }
            int local = op.SubjectIndex - chunk.DestinationBase;
            if (local < 0 || local >= slice.DestinationFiles.Count)
            {
                // Defensive: a Deleted op whose subject cannot be resolved would otherwise become a
                // deletion with no size/timestamp to re-verify against. Drop it and say so — an
                // unexplained missing deletion is far better than an unverifiable one.
                _logger.LogWarning(
                    "Run snapshot: a destination deletion for \"{Path}\" could not be paired with its file " +
                    "(subject {Subject}, base {Base}, {Count} files in chunk) and was dropped",
                    op.Path, op.SubjectIndex, chunk.DestinationBase, slice.DestinationFiles.Count);
                continue;
            }
            IPhysicalFileView file = slice.DestinationFiles[local];
            // The delete half is the SECOND segment of the destination side's single display index
            // space — see RunSnapshotOrder.Builder.SecondSegmentRow.
            _deleteBlocks.Row(_deletes?.Position ?? 0);
            _destinationOrder.SecondSegmentRow(file.Path, file.Root);
            Write(_deletes, RunSnapshotJsonContext.Default.RunDeleteItem, new RunDeleteItem
            {
                Path = file.Path,
                TargetRoot = file.Root,
                SizeBytes = file.Length,
                LastWriteUtc = file.LastWritten,
            });
            DeleteCount++;
            DeleteBytes += file.Length;
            CountSwept(file.Root);
            CountOrphan(file.Root);
            // An orphan is a destination ROW as well as a deletion, so it belongs in the facet count
            // alongside the projection rows — the Destinations tab shows both.
            Bump(_destinationRowsByRoot, file.Root);
        }

        // Per chunk, not per row: the counter is what paces the sample, and a chunk boundary is a point
        // at which the files on disk are coherent — the same reason RunCoordinator pauses only here.
        _rowsWritten = SourceCount + DestinationCount + CopyCount + DeleteCount;
        CheckDiskSpace();
    }

    /// <summary>Records one non-orphan destination operation. Both index resolutions are best-effort by
    /// design: a projection row that cannot be placed is dropped, because this half is what the plan is
    /// SHOWN as and never what it does. Contrast the <c>Deleted</c> branch above, which logs a warning
    /// when it drops one — an unpaired deletion is a safety matter, an unpaired display row is not.</summary>
    private void WriteDestination(IFileOperationView op, PlanChunk chunk, DryRunChunk slice)
    {
        // The plan's own global source index IS the ordinal, because sources.ndjsonl holds every scanned
        // source in plan order. The sweep's own finds name no source and keep -1.
        int ordinal = op.SourceIndex;

        // The pre-existing file, when there is one. Its path is NOT necessarily the op's: a Rename's path
        // is the suffixed new name while its subject is the file that forced the suffix.
        string? subjectPath = null;
        long? subjectSize = null;
        DateTimeOffset? subjectWritten = null;
        if (op.SubjectIndex >= 0)
        {
            int localSubject = op.SubjectIndex - chunk.DestinationBase;
            if (localSubject >= 0 && localSubject < slice.DestinationFiles.Count)
            {
                IPhysicalFileView subject = slice.DestinationFiles[localSubject];
                subjectPath = subject.Path;
                subjectSize = subject.Length;
                subjectWritten = subject.LastWritten;
            }
        }

        // The resulting file's size. For a write that is the INCOMING content's — the source file's, which
        // only this pass can see, because a page of the destination half never contains the source half.
        // Falls back to the pre-existing file for an untouched entry, and to 0 when neither resolves in
        // this chunk (which renders as "0 B", the same as a genuinely empty file).
        long size = subjectSize ?? 0;
        int localSource = ordinal - chunk.SourceBase;
        if ((uint)localSource < (uint)slice.SourceFiles.Count)
            size = slice.SourceFiles[localSource].Length;

        _destinationBlocks.Row(_destinations?.Position ?? 0);
        _destinationOrder.Row(op.Path, op.Root);
        Write(_destinations, RunSnapshotJsonContext.Default.RunDestinationItem, new RunDestinationItem
        {
            Path = op.Path,
            TargetRoot = op.Root,
            Kind = op.Kind,
            SourceOrdinal = ordinal,
            Detail = op.Detail,
            SizeBytes = size,
            SubjectPath = subjectPath,
            SubjectSizeBytes = subjectSize,
            SubjectLastWriteUtc = subjectWritten,
        });
        DestinationCount++;
        Bump(_destinationRowsByRoot, op.Root);
        if (DescribesExistingFile(op.Kind))
            CountSwept(op.Root);
    }

    /// <summary>Builds the header from everything this writer tallied during the walk, plus the few
    /// facts only the caller knows (which run, which profile, when, and how the plan ended).
    ///
    /// <para><b>Here rather than at the call site, because there were two call sites and they drifted.</b>
    /// The coordinator and the test harness each constructed a header field by field, so every count
    /// added to this writer had to be remembered in both — and the moment one was not, the tests were
    /// asserting against a header the production path would have filled in. Nineteen of the header's
    /// fields come from this object; the caller supplies five.</para></summary>
    public RunSnapshotHeader Header(
        Guid runId, Profile profile, string? scopePath, DateTimeOffset plannedAt, PlanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new RunSnapshotHeader
        {
            RunId = runId,
            Profile = profile,
            ScopePath = scopePath,
            // The same instant reported to clients as RunSummaryDto.PlannedAtUtc, not a second call: a
            // client measures a stored preview's staleness against that value, and the snapshot is what
            // it is measuring the age OF.
            PlannedAtUtc = plannedAt,
            CopyItemCount = CopyCount,
            DeleteItemCount = DeleteCount,
            CopyBytes = CopyBytes,
            DeleteBytes = DeleteBytes,
            SourceItemCount = SourceCount,
            DestinationItemCount = DestinationCount,
            OverwriteCount = OverwriteCount,
            RenameCount = RenameCount,
            DisposalCount = DisposalCount,
            SweptFilesByTargetRoot = SweptByTargetRoot,
            OrphansByTargetRoot = OrphansByTargetRoot,
            SourceRowsByRoot = SourceRowsByRoot,
            DestinationRowsByRoot = DestinationRowsByRoot,
            UntouchedCount = UntouchedCount,
            ProcessedCount = ProcessedCount,
            DestinationRowsByKind = DestinationRowsByKind,
            Truncated = state.Truncated,
            SweepFaultDetail = state.SweepFaultDetail,
            Space = state.Space,
        };
    }

    /// <summary>Flushes the item files and writes the header. Call once, after the plan stream has
    /// completed; the header's presence is what marks a snapshot as complete and executable.</summary>
    public Result Complete(RunSnapshotHeader header)
    {
        if (_failure is not null)
            return _failure;
        try
        {
            _copies!.Flush(flushToDisk: false);
            _deletes!.Flush(flushToDisk: false);
            _sources!.Flush(flushToDisk: false);
            _destinations!.Flush(flushToDisk: false);

            // Sidecars BEFORE the header, and best-effort. The header's presence is what marks the
            // snapshot complete and executable, so it must not appear until everything a reader may
            // find is settled — but a sidecar that could not be written is a degraded VIEW of a sound
            // plan (a scan instead of a seek, plan order instead of sorted), so it is logged and the run
            // continues. The preview handles all four being absent, which is also what it sees for a
            // snapshot written before paging existed.
            WriteSidecars();

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(header, RunSnapshotJsonContext.Default.RunSnapshotHeader);
            string path = Path.Combine(_directory, RunSnapshotPaths.HeaderFileName);
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Write(json, 0, json.Length);
            stream.Flush(flushToDisk: true);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not complete the run snapshot at {Directory}", _directory);
            return $"could not write the run snapshot header: {ex.Message}";
        }
    }

    /// <summary>Writes the four paging sidecars, logging rather than failing. See the call site for why
    /// a sidecar failure is not a plan failure.</summary>
    private void WriteSidecars()
    {
        Emit("source block index", _sourceBlocks.Write(In(RunSnapshotPaths.SourcesIndexFileName)));
        Emit("destination block index", _destinationBlocks.Write(In(RunSnapshotPaths.DestinationsIndexFileName)));
        Emit("delete block index", _deleteBlocks.Write(In(RunSnapshotPaths.DeletesIndexFileName)));
        Emit("source order", _sourceOrder.Write(In(RunSnapshotPaths.SourcesOrderFileName)));
        Emit("destination order", _destinationOrder.Write(In(RunSnapshotPaths.DestinationsOrderFileName)));

        string In(string name) => Path.Combine(_directory, name);

        void Emit(string what, string? error)
        {
            if (error is not null)
                _logger.LogWarning(
                    "Run snapshot at {Directory}: the {What} could not be written ({Error}); the preview " +
                    "falls back to an unindexed read", _directory, what, error);
        }
    }

    private void Write<T>(FileStream? stream, JsonTypeInfo<T> typeInfo, T item)
    {
        if (stream is null)
            return;
        try
        {
            byte[] line = NdjsonFrame.Encode(JsonSerializer.SerializeToUtf8Bytes(item, typeInfo));
            stream.Write(line, 0, line.Length);
        }
        catch (Exception ex)
        {
            // First failure wins and stops further writes: a half-written work list must never be
            // mistaken for a complete one.
            _failure ??= $"could not write a run snapshot item: {ex.Message}";
            _logger.LogError(ex, "Could not write a run snapshot item under {Directory}", _directory);
        }
    }

    public void Dispose()
    {
        // Before the streams: a plan abandoned mid-walk (cancelled, failed) never reached Complete, so
        // this is the only thing that removes its spilled sort runs.
        _sourceOrder.Dispose();
        _destinationOrder.Dispose();
        _copies?.Dispose();
        _deletes?.Dispose();
        _sources?.Dispose();
        _destinations?.Dispose();
        _copies = null;
        _deletes = null;
        _sources = null;
        _destinations = null;
    }
}

/// <summary>File names inside a run's snapshot directory. One place so the writer, the reader, and the
/// startup sweep agree.</summary>
internal static class RunSnapshotPaths
{
    public const string HeaderFileName = "plan.json";
    public const string CopiesFileName = "copies.ndjsonl";
    public const string DeletesFileName = "deletes.ndjsonl";
    public const string SourcesFileName = "sources.ndjsonl";
    public const string DestinationsFileName = "destinations.ndjsonl";

    // Paging sidecars for the two DISPLAY halves. ".idx" is ordinal → byte offset (every 512th row);
    // ".ord" is display position → ordinal. Neither exists for a snapshot written before paging, and
    // both are optional at read time — see RunSnapshotBlockIndex and RunSnapshotOrder.
    public const string SourcesIndexFileName = "sources.idx";
    public const string DestinationsIndexFileName = "destinations.idx";
    public const string DeletesIndexFileName = "deletes.idx";
    public const string SourcesOrderFileName = "sources.ord";
    public const string DestinationsOrderFileName = "destinations.ord";

    public static string DirectoryFor(EnginePaths paths, Guid runId) =>
        Path.Combine(paths.RunsDirectory, runId.ToString("N"));
}

/// <summary>Reads a snapshot back. Both item readers STREAM: the copy list of a large run is the
/// reason the snapshot exists on disk at all, so materializing it to enqueue from would defeat the
/// purpose.</summary>
internal static class RunSnapshotReader
{
    public static Result<RunSnapshotHeader, string> ReadHeader(string runDirectory)
    {
        string path = Path.Combine(runDirectory, RunSnapshotPaths.HeaderFileName);
        try
        {
            if (!File.Exists(path))
                return $"the run snapshot header \"{path}\" does not exist";
            byte[] json = File.ReadAllBytes(path);
            RunSnapshotHeader? header = JsonSerializer.Deserialize(
                json, RunSnapshotJsonContext.Default.RunSnapshotHeader);
            return header is null
                ? $"the run snapshot header \"{path}\" is empty"
                : Result<RunSnapshotHeader, string>.Success(header);
        }
        catch (Exception ex)
        {
            return $"could not read the run snapshot header \"{path}\": {ex.Message}";
        }
    }

    public static IEnumerable<RunCopyItem> ReadCopies(string runDirectory, ILogger logger) =>
        Read(Path.Combine(runDirectory, RunSnapshotPaths.CopiesFileName),
            RunSnapshotJsonContext.Default.RunCopyItem, logger);

    public static IEnumerable<RunDeleteItem> ReadDeletes(string runDirectory, ILogger logger) =>
        Read(Path.Combine(runDirectory, RunSnapshotPaths.DeletesFileName),
            RunSnapshotJsonContext.Default.RunDeleteItem, logger);

    /// <summary>Every source the plan looked at, in plan order, so an item's ordinal is its source index.
    /// A missing file yields nothing, which is the correct reading of a snapshot written before the display
    /// halves existed.</summary>
    public static IEnumerable<RunSourceItem> ReadSources(string runDirectory, ILogger logger) =>
        Read(Path.Combine(runDirectory, RunSnapshotPaths.SourcesFileName),
            RunSnapshotJsonContext.Default.RunSourceItem, logger);

    /// <summary>Reads specific ROWS by ordinal, seeking to each one's block rather than scanning to it.
    ///
    /// <para>This is the read half of paging: <paramref name="index"/> gives the byte offset of every
    /// 512th row, so reaching row 4,000,000 costs a seek plus at most 511 line skips instead of four
    /// million deserializations. Without it a windowed preview would be slower than the whole-file read
    /// it replaced.</para>
    ///
    /// <para><paramref name="ordinals"/> need not be contiguous or sorted — a sorted view hands them in
    /// display order, which is scattered across the file by construction. They ARE grouped by block
    /// internally so a page whose rows share a block pays one seek for all of them, which is the common
    /// case: the order file sorts by relative path, and files in one directory were written
    /// together.</para>
    ///
    /// <para>Returns rows in the order asked for, with a null for any ordinal that could not be read.
    /// A null is a hole in the page, never an exception: one unreadable row must not blank a
    /// preview.</para></summary>
    public static T?[] ReadByOrdinal<T>(
        string path, RunSnapshotBlockIndex.Index index, IReadOnlyList<int> ordinals,
        JsonTypeInfo<T> typeInfo, ILogger logger)
        where T : class
    {
        T?[] rows = new T?[ordinals.Count];
        if (ordinals.Count == 0 || !File.Exists(path))
            return rows;

        // Group the requested positions by the block they live in, then walk the blocks in file order:
        // one seek and one forward pass per block, however the caller shuffled the ordinals.
        Dictionary<long, List<int>> byBlock = [];
        for (int i = 0; i < ordinals.Count; i++)
        {
            long block = ordinals[i] / RunSnapshotBlockIndex.BlockRows;
            (byBlock.TryGetValue(block, out List<int>? slots) ? slots : byBlock[block] = []).Add(i);
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            List<long> blocks = [.. byBlock.Keys];
            blocks.Sort();   // file order, so the seeks only ever go forward
            foreach (long block in blocks)
            {
                List<int> slots = byBlock[block];
                (long offset, int _) = index.Locate(block * RunSnapshotBlockIndex.BlockRows);
                stream.Seek(offset, SeekOrigin.Begin);

                // Wanted rows within this block, by their offset from its first row, so the forward scan
                // can stop as soon as the last one is in hand.
                Dictionary<int, List<int>> wanted = [];
                int furthest = -1;
                foreach (int slot in slots)
                {
                    int within = (int)(ordinals[slot] % RunSnapshotBlockIndex.BlockRows);
                    (wanted.TryGetValue(within, out List<int>? at) ? at : wanted[within] = []).Add(slot);
                    furthest = Math.Max(furthest, within);
                }

                using StreamLineReader lines = new(stream);
                for (int within = 0; within <= furthest; within++)
                {
                    if (!lines.TryNextLine(out ReadOnlySpan<byte> line))
                        break;
                    if (!wanted.TryGetValue(within, out List<int>? slotsHere))
                        continue;   // a row between two wanted ones: skipped without deserializing
                    T? item = Decode(line, typeInfo, path, logger);
                    foreach (int slot in slotsHere)
                        rows[slot] = item;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Run snapshot: could not read a page of \"{Path}\"", path);
        }
        return rows;
    }

    /// <summary>The unindexed fallback for <see cref="ReadByOrdinal{T}"/>: one sequential pass over the
    /// file, keeping the rows whose position was asked for.
    ///
    /// <para>Linear in the FILE rather than in the page, which is exactly the cost the block index
    /// exists to remove. It is kept because the alternative is refusing to show a plan that is perfectly
    /// sound — a snapshot written before paging existed, or one whose sidecar write failed, has no
    /// index and must still be viewable.</para></summary>
    public static T?[] ScanByOrdinal<T>(
        string path, IReadOnlyList<int> ordinals, JsonTypeInfo<T> typeInfo, ILogger logger)
        where T : class
    {
        T?[] rows = new T?[ordinals.Count];
        if (ordinals.Count == 0 || !File.Exists(path))
            return rows;

        Dictionary<int, List<int>> wanted = [];
        int furthest = -1;
        for (int i = 0; i < ordinals.Count; i++)
        {
            (wanted.TryGetValue(ordinals[i], out List<int>? slots) ? slots : wanted[ordinals[i]] = []).Add(i);
            furthest = Math.Max(furthest, ordinals[i]);
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using StreamLineReader lines = new(stream);
            for (int ordinal = 0; ordinal <= furthest; ordinal++)
            {
                if (!lines.TryNextLine(out ReadOnlySpan<byte> line))
                    break;
                if (!wanted.TryGetValue(ordinal, out List<int>? slotsHere))
                    continue;
                T? item = Decode(line, typeInfo, path, logger);
                foreach (int slot in slotsHere)
                    rows[slot] = item;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Run snapshot: could not scan \"{Path}\" for a page", path);
        }
        return rows;
    }

    private static T? Decode<T>(ReadOnlySpan<byte> line, JsonTypeInfo<T> typeInfo, string path, ILogger logger)
        where T : class
    {
        try
        {
            if (!NdjsonFrame.TryDecode(line, out ReadOnlySpan<byte> json, out string? reason))
            {
                logger.LogWarning("Run snapshot: skipping a malformed row of \"{Path}\": {Reason}", path, reason);
                return null;
            }
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            logger.LogWarning(ex, "Run snapshot: skipping an unreadable row of \"{Path}\"", path);
            return null;
        }
    }

    /// <summary>The plan's destination projection. A missing file yields nothing, which is the correct
    /// reading of a snapshot written before this half existed.</summary>
    public static IEnumerable<RunDestinationItem> ReadDestinations(string runDirectory, ILogger logger) =>
        Read(Path.Combine(runDirectory, RunSnapshotPaths.DestinationsFileName),
            RunSnapshotJsonContext.Default.RunDestinationItem, logger);

    /// <summary>Streams framed NDJSON items. A line that fails its CRC or will not deserialize is
    /// SKIPPED with a warning rather than failing the read.
    /// <para>That choice is safe in one direction only, and it is the direction that matters: a
    /// dropped copy item means a file is not copied this run (the next run picks it up), and a dropped
    /// delete item means an orphan survives. Both are recoverable. The unsafe direction — inventing or
    /// mis-reading a deletion — is what the checksum prevents.</para></summary>
    private static IEnumerable<T> Read<T>(string path, JsonTypeInfo<T> typeInfo, ILogger logger)
        where T : class
    {
        if (!File.Exists(path))
            yield break;

        // Bytes, not text. This used to decode each line to a string with StreamReader.ReadLine and then
        // immediately re-encode it with Encoding.UTF8.GetBytes, because NdjsonFrame and the deserializer
        // both want bytes — two full transcodes and two allocations per row to undo work the file already
        // had in the right form. This path runs on EVERY plan replay over sources and destinations alike,
        // so at the 500,000-file cap that was millions of throwaway objects per preview.
        int lineNumber = 0;
        foreach (byte[] line in NdjsonLines.ReadAllLines(path))
        {
            lineNumber++;
            T? item = null;
            string? problem = null;
            try
            {
                if (!NdjsonFrame.TryDecode(line, out ReadOnlySpan<byte> json, out string? reason))
                    problem = reason ?? "malformed frame";
                else
                    item = JsonSerializer.Deserialize(json, typeInfo);
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
            if (item is null)
            {
                logger.LogWarning(
                    "Run snapshot: skipping unreadable item at line {Line} of \"{Path}\": {Problem}",
                    lineNumber, path, problem ?? "empty item");
                continue;
            }
            yield return item;
        }
    }
}

/// <summary>Source-generated serialization for the snapshot. Options match
/// <c>FileManagerJsonContext</c>'s so the embedded <see cref="Profile"/> round-trips exactly as it
/// does in <c>profiles.json</c> — string enums included, which also keeps the file readable when
/// diagnosing a run after the fact and immune to enum reordering.</summary>
[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunSnapshotHeader))]
[JsonSerializable(typeof(RunCopyItem))]
[JsonSerializable(typeof(RunDeleteItem))]
[JsonSerializable(typeof(RunSourceItem))]
[JsonSerializable(typeof(RunDestinationItem))]
internal sealed partial class RunSnapshotJsonContext : JsonSerializerContext;
