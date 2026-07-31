using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Relocates the profiles storage directory (§9). Optionally moves the existing profile
/// files (GUID-named *.json only — never unrelated files) into the new directory, persists the new
/// location to settings, then reloads the catalog so the in-memory snapshot reflects the new
/// directory. The move is copy-then-delete with the settings persist as the commit point: a failure
/// before the persist rolls the copies back and leaves the old state fully intact; the originals
/// are deleted only after the switch is durable. Destination collisions are never overwritten —
/// they are skipped and reported in <see cref="RelocateProfilesResponse.SkippedFiles"/> so the
/// caller can tell the user a (possibly stale) copy won. An ErrorResponse means a validation or
/// I/O fault; success carries the persisted settings plus the moved/skipped outcome.</summary>
public sealed class RelocateProfilesHandler(
    ILogger<RelocateProfilesHandler> logger, ISettingsProvider settings, IProfileCatalog catalog)
    : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.RelocateProfiles;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (RelocateProfilesRequest)request;

        string requested = typed.NewDirectory?.Trim() ?? "";
        if (requested.Length == 0)
            return Fail("PROFILES_RELOCATE_FAILED", "the profiles directory cannot be empty");

        // Everything from here runs inside the try: even path normalization can throw (an embedded
        // NUL is valid JSON), and that must surface as PROFILES_RELOCATE_FAILED, not a faulted caller.
        string newDir = requested;
        try
        {
            if (!Path.IsPathFullyQualified(requested))
                return Fail("PROFILES_RELOCATE_FAILED", "the profiles directory must be an absolute path");

            string oldDir = settings.Current.ProfilesDirectory;
            newDir = Path.GetFullPath(requested);

            // No-op when the location is unchanged (case/trailing-separator insensitive on Windows).
            if (string.Equals(Normalize(oldDir), Normalize(newDir), StringComparison.OrdinalIgnoreCase))
            {
                IpcResponse unchanged = new RelocateProfilesResponse { Settings = settings.Current };
                return Task.FromResult(unchanged);
            }

            Directory.CreateDirectory(newDir);

            // Phase 1 — copy (never move) the profile files across. Only GUID-named *.json files are
            // profiles (ProfileStore.ProfileFilePath); anything else in the folder is left alone. A
            // failure mid-copy rolls back the copies already made — the old directory, settings, and
            // catalog are untouched, so the caller can just retry.
            List<string> copied = [];
            List<string> originals = [];
            List<string> skipped = [];
            if (typed.MoveExisting && Directory.Exists(oldDir))
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(oldDir, "*.json"))
                    {
                        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out _))
                            continue;   // not a profile file (e.g. a stray settings.json) — never sweep it

                        string name = Path.GetFileName(file);
                        string dest = Path.Combine(newDir, name);
                        if (File.Exists(dest))
                        {
                            logger.LogWarning(
                                "Not moving {File}: a file already exists at {Dest}; leaving the original in place", file, dest);
                            skipped.Add(name);
                            continue;
                        }
                        File.Copy(file, dest);
                        copied.Add(dest);
                        originals.Add(file);
                    }
                }
                catch
                {
                    RollBackCopies(copied);
                    throw;
                }
            }

            // Phase 2 — the commit point: persist the new directory. On failure, roll the copies
            // back; the old state stays fully consistent.
            var updated = settings.Update(settings.Current with { ProfilesDirectory = newDir });
            if (updated.TryGetError(out string? saveError))
            {
                logger.LogWarning("Relocate profiles could not persist the new directory: {Error}", saveError);
                RollBackCopies(copied);
                IpcResponse failure = new ErrorResponse { Code = "PROFILES_RELOCATE_FAILED", Message = saveError };
                return Task.FromResult(failure);
            }
            updated.TryGetValue(out GlobalSettings? saved);

            // Phase 3 — the switch is durable: delete the originals (best-effort; a leftover in the
            // old directory is harmless because nothing points at it any more, and it is skip-listed
            // if the user ever relocates back).
            foreach (string original in originals)
            {
                try { File.Delete(original); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not delete {File} after relocating profiles; the copy in {NewDir} is authoritative",
                        original, newDir);
                }
            }

            // Phase 4 — point the in-memory catalog at the new directory. Settings and files already
            // switched, so a reload failure must NOT report success: the catalog would keep serving
            // the old directory's snapshot until some later reload.
            if (CatalogReloadGuard.Check(catalog, logger, "PROFILES_RELOCATE_FAILED", $"relocated to \"{newDir}\"")
                is { } reloadFailure)
                return Task.FromResult<IpcResponse>(reloadFailure);

            logger.LogInformation(
                "Relocated profiles directory to {NewDir} (moved {Moved}, skipped {Skipped} file(s), MoveExisting={Move})",
                newDir, copied.Count, skipped.Count, typed.MoveExisting);

            IpcResponse response = new RelocateProfilesResponse
            {
                Settings = saved!,
                MovedCount = copied.Count,
                SkippedFiles = skipped,
            };
            return Task.FromResult(response);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(ex, "Could not relocate profiles to {NewDir}", newDir);
            return Fail("PROFILES_RELOCATE_FAILED", $"could not relocate profiles: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Relocating profiles to {NewDir} failed unexpectedly", newDir);
            return Fail("PROFILES_RELOCATE_FAILED", $"could not relocate profiles: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Best-effort removal of the copies made before a failed commit. A leftover copy is
    /// only cosmetic (nothing points at the new directory), so failures are logged, never thrown.</summary>
    private void RollBackCopies(List<string> copied)
    {
        foreach (string copy in copied)
        {
            try { File.Delete(copy); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not roll back copied profile file {File}", copy);
            }
        }
    }

    private static Task<IpcResponse> Fail(string code, string message) =>
        Task.FromResult<IpcResponse>(new ErrorResponse { Code = code, Message = message });

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
