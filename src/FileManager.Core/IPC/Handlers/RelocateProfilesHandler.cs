using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Relocates the profiles storage directory (§9). Optionally moves the existing *.json files
/// into the new directory, persists the new location to settings, then reloads the catalog so the
/// in-memory snapshot reflects the new directory. Transactional relative to the generic settings save:
/// the move + persist + reload happen together, keyed off the request's MoveExisting flag. An
/// ErrorResponse means a validation or I/O fault; success carries the persisted settings.</summary>
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
        if (!Path.IsPathFullyQualified(requested))
            return Fail("PROFILES_RELOCATE_FAILED", "the profiles directory must be an absolute path");

        string oldDir = settings.Current.ProfilesDirectory;
        string newDir = Path.GetFullPath(requested);

        // No-op when the location is unchanged (case/trailing-separator insensitive on Windows).
        if (string.Equals(Normalize(oldDir), Normalize(newDir), StringComparison.OrdinalIgnoreCase))
        {
            IpcResponse unchanged = new SettingsResponse { Settings = settings.Current };
            return Task.FromResult(unchanged);
        }

        try
        {
            Directory.CreateDirectory(newDir);

            int moved = 0;
            if (typed.MoveExisting && Directory.Exists(oldDir))
            {
                foreach (string file in Directory.EnumerateFiles(oldDir, "*.json"))
                {
                    string dest = Path.Combine(newDir, Path.GetFileName(file));
                    if (File.Exists(dest))
                    {
                        logger.LogWarning(
                            "Not moving {File}: a file already exists at {Dest}; leaving the original in place", file, dest);
                        continue;
                    }
                    File.Move(file, dest);
                    moved++;
                }
            }

            var updated = settings.Update(settings.Current with { ProfilesDirectory = newDir });
            if (updated.TryGetError(out string? saveError))
            {
                logger.LogWarning("Relocate profiles could not persist the new directory: {Error}", saveError);
                IpcResponse failure = new ErrorResponse { Code = "PROFILES_RELOCATE_FAILED", Message = saveError };
                return Task.FromResult(failure);
            }
            updated.TryGetValue(out GlobalSettings? saved);

            // Point the in-memory catalog at the new directory.
            var reload = catalog.Reload();
            if (reload.TryGetError(out string? reloadError))
                logger.LogWarning("Catalog reload after relocation failed: {Error}", reloadError);

            logger.LogInformation(
                "Relocated profiles directory to {NewDir} (moved {Moved} file(s), MoveExisting={Move})",
                newDir, moved, typed.MoveExisting);

            IpcResponse response = new SettingsResponse { Settings = saved! };
            return Task.FromResult(response);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

    private static Task<IpcResponse> Fail(string code, string message) =>
        Task.FromResult<IpcResponse>(new ErrorResponse { Code = code, Message = message });

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
