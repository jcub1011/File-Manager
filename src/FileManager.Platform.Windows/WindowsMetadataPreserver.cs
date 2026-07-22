using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace FileManager.Platform.Windows;

/// <summary>Best-effort timestamp + ACL preservation (spec §6.4). <see cref="Inspect"/> detects a
/// lossy transition before the copy (e.g. NTFS → exFAT drops ACLs and alternate data streams);
/// <see cref="Apply"/> stamps the verified temp before the rename. Under
/// <see cref="MetadataOnConflict.FailJob"/> a detected loss becomes a placement failure.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMetadataPreserver(ILogger<WindowsMetadataPreserver> logger) : IMetadataPreserver
{
    private static readonly HashSet<string> AclCapable = new(StringComparer.OrdinalIgnoreCase) { "NTFS", "ReFS" };

    public Result<MetadataLossReport, string> Inspect(string sourcePath, string targetDirectory)
    {
        try
        {
            var details = new List<string>();
            string? sourceFormat = TryDriveFormat(sourcePath);
            string? targetFormat = TryDriveFormat(targetDirectory);

            if (sourceFormat is not null && targetFormat is not null)
            {
                if (AclCapable.Contains(sourceFormat) && !AclCapable.Contains(targetFormat))
                    details.Add($"target filesystem {targetFormat} cannot preserve NTFS ACLs or alternate data streams");
                if (targetFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || targetFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
                    details.Add($"target filesystem {targetFormat} has reduced timestamp resolution");
            }

            return new MetadataLossReport(details.Count > 0, details);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Metadata inspection failed for {Source} → {Target}", sourcePath, targetDirectory);
            // Inspection is best-effort; an inspection failure is not itself a metadata loss.
            return new MetadataLossReport(false, []);
        }
    }

    public Result Apply(string fromPath, string toPath, MetadataOnConflict onConflict)
    {
        // Timestamps: best-effort, never fatal (rounding on the destination FS is expected).
        try
        {
            File.SetCreationTimeUtc(toPath, File.GetCreationTimeUtc(fromPath));
            File.SetLastWriteTimeUtc(toPath, File.GetLastWriteTimeUtc(fromPath));
            File.SetLastAccessTimeUtc(toPath, File.GetLastAccessTimeUtc(fromPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not copy timestamps {From} → {To}", fromPath, toPath);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): the timestamp copy is best-effort and never fatal,
            // so an unexpected fault (e.g. ArgumentException on a malformed path) is logged, not thrown
            // out of Apply — the caller invokes this synchronously with no surrounding guard.
            logger.LogWarning(ex, "Unexpected error copying timestamps {From} → {To}", fromPath, toPath);
        }

        // ACLs: best-effort. A failure is a genuine metadata loss — fail the job under FailJob,
        // otherwise warn and continue (the temp keeps the target directory's inherited ACL).
        try
        {
            var source = new FileInfo(fromPath);
            FileSecurity security = source.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            new FileInfo(toPath).SetAccessControl(security);
            return Result.Success();
        }
        catch (Exception ex)
        {
            if (onConflict == MetadataOnConflict.FailJob)
            {
                logger.LogWarning(ex, "ACL preservation failed and MetadataOnConflict is FailJob: {To}", toPath);
                return $"could not preserve ACLs for \"{toPath}\": {ex.Message}";
            }
            logger.LogInformation("ACL preservation skipped (best-effort) for {To}: {Reason}", toPath, ex.Message);
            return Result.Success();
        }
    }

    private string? TryDriveFormat(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            // DriveInfo throws on UNC roots; treat unknown as "cannot determine" (no loss claimed).
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            return new DriveInfo(root).DriveFormat;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): the format probe is a best-effort refinement and
            // must degrade to "cannot determine", logged, never a throw out of metadata handling.
            logger.LogWarning(ex, "Drive-format probe failed unexpectedly for {Path}", path);
            return null;
        }
    }
}
