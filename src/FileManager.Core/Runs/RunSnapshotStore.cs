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

/// <summary>Writes one run's frozen work list as the plan streams in, so a 500,000-file run never
/// holds its work list in memory.
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
/// they roughly double a snapshot's size; they are bounded by the same file cap the plan is, and the whole
/// directory is deleted when the run closes.</para>
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
    private readonly string _directory;
    private readonly ILogger _logger;
    private FileStream? _copies;
    private FileStream? _deletes;
    private FileStream? _sources;
    private FileStream? _destinations;
    private string? _failure;

    public RunSnapshotWriter(string runDirectory, ILogger logger)
    {
        _directory = runDirectory;
        _logger = logger;
        try
        {
            Directory.CreateDirectory(runDirectory);
            _copies = Open(RunSnapshotPaths.CopiesFileName);
            _deletes = Open(RunSnapshotPaths.DeletesFileName);
            _sources = Open(RunSnapshotPaths.SourcesFileName);
            _destinations = Open(RunSnapshotPaths.DestinationsFileName);
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

    public int CopyCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int SourceCount { get; private set; }
    public int DestinationCount { get; private set; }
    public long CopyBytes { get; private set; }
    public long DeleteBytes { get; private set; }

    /// <summary>Every pre-existing file the destination sweep classified, tallied by the target root it
    /// sits under — survivors and orphans together.
    /// <para>This is the denominator the Mirror deletion pass's ratio guard needs, and the only place it
    /// can be counted honestly: the sweep sees each destination file exactly once, here. Reconstructing
    /// it downstream from the copy items cannot work, because an already-identical file is
    /// <see cref="OperationKind.SkippedUnchanged"/> and produces no copy item at all — so a
    /// synchronized profile's denominator collapsed to its own orphan count and the guard refused every
    /// steady-state pass at 100%.</para></summary>
    public IReadOnlyDictionary<string, int> SweptByTargetRoot => _sweptByTargetRoot;

    private readonly Dictionary<string, int> _sweptByTargetRoot = new(StringComparer.OrdinalIgnoreCase);

    private void CountSwept(string targetRoot) =>
        _sweptByTargetRoot[targetRoot] = _sweptByTargetRoot.GetValueOrDefault(targetRoot) + 1;

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

        // The display source list: EVERY scanned file, whatever the plan decided about it, in plan order —
        // so an item's ordinal is the source index the plan assigned it and a destination row can name it.
        // Driven off the files rather than the operations because the files are the row set: a file whose
        // operation went missing must still appear, or it silently vanishes from the view.
        for (int i = 0; i < slice.SourceFiles.Count; i++)
        {
            IPhysicalFileView file = slice.SourceFiles[i];
            IFileOperationView? op = i < slice.SourceOperations.Count ? slice.SourceOperations[i] : null;
            Write(_sources, RunSnapshotJsonContext.Default.RunSourceItem, new RunSourceItem
            {
                Path = file.Path,
                SourceRoot = file.Root,
                SizeBytes = file.Length,
                LastWriteUtc = file.LastWritten,
                Kind = op?.Kind ?? OperationKind.Unknown,
                Disposition = op?.SourceDisposition,
                Detail = op?.Detail,
            });
            SourceCount++;
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
        }
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

        Write(_destinations, RunSnapshotJsonContext.Default.RunDestinationItem, new RunDestinationItem
        {
            Path = op.Path,
            TargetRoot = op.Root,
            Kind = op.Kind,
            SourceOrdinal = ordinal,
            Detail = op.Detail,
            SubjectPath = subjectPath,
            SubjectSizeBytes = subjectSize,
            SubjectLastWriteUtc = subjectWritten,
        });
        DestinationCount++;
        if (DescribesExistingFile(op.Kind))
            CountSwept(op.Root);
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
