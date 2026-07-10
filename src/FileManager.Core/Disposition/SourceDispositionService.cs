using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Audit;
using FileManager.Core.Jobs;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;

namespace FileManager.Core.Disposition;

/// <summary>Applies <c>OnSuccess</c> (spec §4 Phase 6) after — and only after — <c>job-committed</c>
/// is durable (I-DISPOSE). A disposition failure is returned for the caller to record in
/// <c>job-closed</c>; it is never rolled back (the copies are already safe). If any target ended
/// <see cref="TargetState.SkippedConflict"/>, a disposing action is downgraded to KeepSource with a
/// warning — disposing a source not delivered to every target would violate the spirit of I-DISPOSE.
/// (<see cref="TargetState.SatisfiedUnchanged"/> does not downgrade — content is provably in place.)</summary>
public sealed class SourceDispositionService(
    ITrashService trash,
    IDispositionAuditLog audit,
    TimeProvider time,
    ILogger<SourceDispositionService> logger) : ISourceDispositionService
{
    public Result<DispositionAuditRecord, JobError> Dispose(JobExecution execution)
    {
        JobPlan plan = execution.Plan;
        bool anySkipped = execution.Targets.Any(t => t.State == TargetState.SkippedConflict);
        string? archiveDest = plan.Policies.OnSuccess == OnSuccessAction.MoveToArchive
            ? ResolveArchiveDestination(plan.Source.Path, plan.Policies.ArchiveFolder, plan.Profile.TargetLayout, plan.Payload.SourceRoot)
            : null;
        return Apply(plan.JobId.Value, plan.Source.Path, plan.Policies.OnSuccess, plan.Policies.ArchiveFolder, archiveDest, anySkipped);
    }

    public Result<DispositionAuditRecord, JobError> Dispose(
        Guid jobId, SourceSnapshot source, PolicySnapshot policies, bool anyTargetSkippedConflict)
    {
        // Recovery lacks the profile's TargetLayout and source root, so archive falls back to a
        // flat filename-into-folder placement (§7.3 row I is a rare re-attempt).
        string? archiveDest = policies.OnSuccess == OnSuccessAction.MoveToArchive && policies.ArchiveFolder is not null
            ? Path.Combine(policies.ArchiveFolder, Path.GetFileName(source.Path))
            : null;
        return Apply(jobId, source.Path, policies.OnSuccess, policies.ArchiveFolder, archiveDest, anyTargetSkippedConflict);
    }

    private Result<DispositionAuditRecord, JobError> Apply(
        Guid jobId, string sourcePath, OnSuccessAction action, string? archiveFolder, string? archiveDest, bool anySkipped)
    {
        DateTimeOffset now = time.GetUtcNow();

        if (anySkipped && action is OnSuccessAction.MoveToTrash or OnSuccessAction.MoveToArchive or OnSuccessAction.PermanentDelete)
        {
            logger.LogWarning("Downgrading {Action} to KeepSource for \"{Path}\": a target ended SkippedConflict", action, sourcePath);
            return new DispositionAuditRecord(jobId, sourcePath, OnSuccessAction.KeepSource, null, now);
        }

        if (action == OnSuccessAction.KeepSource)
            return new DispositionAuditRecord(jobId, sourcePath, OnSuccessAction.KeepSource, null, now);

        // Idempotent for recovery: a source already gone is treated as disposed.
        if (!File.Exists(sourcePath))
        {
            logger.LogInformation("Source \"{Path}\" already gone; assuming {Action} completed", sourcePath, action);
            return Record(jobId, sourcePath, action, DestinationFor(action, archiveDest), now, append: false);
        }

        try
        {
            switch (action)
            {
                case OnSuccessAction.MoveToTrash:
                    Result trashed = trash.MoveToTrash(sourcePath);
                    if (trashed.TryGetError(out string? trashError))
                        return Failure(sourcePath, $"move to Recycle Bin failed: {trashError}");
                    return Record(jobId, sourcePath, action, "RecycleBin", now, append: true);

                case OnSuccessAction.MoveToArchive:
                    if (archiveDest is null)
                        return Failure(sourcePath, "MoveToArchive with no ArchiveFolder");
                    Directory.CreateDirectory(Path.GetDirectoryName(archiveDest)!);
                    File.Move(sourcePath, archiveDest, overwrite: false);
                    return Record(jobId, sourcePath, action, archiveDest, now, append: true);

                case OnSuccessAction.PermanentDelete:
                    File.Delete(sourcePath);
                    return Record(jobId, sourcePath, action, null, now, append: true);

                default:
                    return Failure(sourcePath, $"unknown OnSuccess action {action}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Disposition {Action} failed for {Path}", action, sourcePath);
            return Failure(sourcePath, $"{action} failed: {ex.Message}");
        }
    }

    private DispositionAuditRecord Record(Guid jobId, string sourcePath, OnSuccessAction action, string? destination, DateTimeOffset now, bool append)
    {
        var record = new DispositionAuditRecord(jobId, sourcePath, action, destination, now);
        if (append)
        {
            Result appended = audit.Append(record);
            if (appended.TryGetError(out string? auditError))
                logger.LogError("Audit append failed for {Path}: {Error}", sourcePath, auditError);   // audit failure never blocks disposition
        }
        return record;
    }

    private static string? DestinationFor(OnSuccessAction action, string? archiveDest) => action switch
    {
        OnSuccessAction.MoveToTrash => "RecycleBin",
        OnSuccessAction.MoveToArchive => archiveDest,
        _ => null,
    };

    private static JobError Failure(string path, string message) =>
        new() { Code = JobErrorCode.DispositionFailed, Message = message, Path = path };

    private static string ResolveArchiveDestination(string sourcePath, string? archiveFolder, TargetLayout layout, string sourceRoot)
    {
        if (archiveFolder is null)
            return sourcePath;   // caller validates; Apply fails cleanly on the null-folder path
        if (layout == TargetLayout.Flatten)
            return Path.Combine(archiveFolder, Path.GetFileName(sourcePath));

        // PreserveStructure: mirror the source's path relative to its watched root under the archive.
        string relative = Path.GetRelativePath(sourceRoot, sourcePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            relative = Path.GetFileName(sourcePath);   // source outside the declared root — fall back to flat
        return Path.Combine(archiveFolder, relative);
    }
}
