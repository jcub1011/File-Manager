using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FileManager.Core.Profiles;

/// <summary>Owns profiles\*.json (§9): load-all at startup, save/delete on IPC requests.
/// Saves validate first and are atomic (write temp + fsync + rename).
/// The Save success channel always carries the validation issues; the write happened iff no
/// Error is present and no BlockingWarning is present without acknowledgeWarnings — callers
/// re-derive "saved" from the severities. The failure string is reserved for I/O and
/// serialization faults.</summary>
public sealed class ProfileStore(ILogger<ProfileStore> logger, ISettingsProvider settings, IProfileValidator validator) : IProfileStore
{
    // Resolved live from settings so a relocation (RelocateProfilesRequest) takes effect on the next
    // read without reconstructing the store. The default matches EnginePaths.ProfilesDirectory.
    private string ProfilesDirectory => settings.Current.ProfilesDirectory;

    public Result<IReadOnlyList<Profile>, string> LoadAll()
    {
        try
        {
            if (!Directory.Exists(ProfilesDirectory))
                return Array.Empty<Profile>();

            List<Profile> profiles = [];
            foreach (string file in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
            {
                // Simplification of §2.4's "load as inactive with a surfaced issue": a corrupt
                // profile file is skipped with a logged warning. Forward-safe — the catalog
                // shape does not change when the richer behavior lands.
                try
                {
                    using FileStream stream = File.OpenRead(file);
                    Profile? profile = JsonSerializer.Deserialize(stream, FileManagerJsonContext.Default.Profile);
                    if (profile is null)
                        logger.LogWarning("Profile file {File} deserialized to null; skipping", file);
                    else
                        profiles.Add(profile);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogWarning(ex, "Profile file {File} could not be loaded; skipping", file);
                }
                catch (Exception ex)
                {
                    // Last resort: unexpected exceptions become logged skips, not faulted callers.
                    logger.LogError(ex, "Profile file {File} failed to load unexpectedly; skipping", file);
                }
            }
            return profiles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not enumerate profiles in {Directory}", ProfilesDirectory);
            return $"could not enumerate profiles: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Enumerating profiles in {Directory} failed unexpectedly", ProfilesDirectory);
            return $"could not enumerate profiles: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public Result<Profile, string> Load(Guid profileId)
    {
        string file = ProfileFilePath(profileId);
        try
        {
            if (!File.Exists(file))
                return $"profile {profileId} does not exist";
            using FileStream stream = File.OpenRead(file);
            Profile? profile = JsonSerializer.Deserialize(stream, FileManagerJsonContext.Default.Profile);
            return profile is null
                ? $"profile file {file} deserialized to null"
                : profile;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not load profile {ProfileId}", profileId);
            return $"could not load profile {profileId}: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Loading profile {ProfileId} failed unexpectedly", profileId);
            return $"could not load profile {profileId}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public Result<IReadOnlyList<ValidationIssue>, string> Save(Profile profile, bool acknowledgeWarnings)
    {
        Result<IReadOnlyList<Profile>, string> all = LoadAll();
        if (all.TryGetError(out string? loadError))
            return $"validation requires the stored profiles, which could not be read: {loadError}";
        all.TryGetValue(out IReadOnlyList<Profile>? stored);

        IReadOnlyList<Profile> otherActive = stored!
            .Where(p => p.Id != profile.Id && p.Active)
            .ToList();
        IReadOnlyList<ValidationIssue> issues = validator.Validate(profile, otherActive);

        bool blocked = issues.Any(i => i.Severity == ValidationSeverity.Error)
            || (!acknowledgeWarnings && issues.Any(i => i.Severity == ValidationSeverity.BlockingWarning));
        if (blocked)
        {
            logger.LogInformation("Save of profile {ProfileId} ({Name}) rejected with {IssueCount} issues",
                profile.Id, profile.Name, issues.Count);
            return Result<IReadOnlyList<ValidationIssue>, string>.Success(issues);
        }

        // Randomized temp name (matching ProfileExport.WriteAtomic): a fixed "<id>.json.tmp" makes
        // two concurrent saves of the same profile collide on a sharing violation, and a crash
        // leaves a name the next save would trip over.
        string? tempPath = null;
        try
        {
            // Resolve the live directory ONCE for the whole write: a concurrent relocation must not
            // tear this operation across the old and new directories mid-save.
            string directory = ProfilesDirectory;
            Directory.CreateDirectory(directory);
            string finalPath = Path.Combine(directory, profile.Id.ToString("D") + ".json");
            tempPath = $"{finalPath}.{Guid.NewGuid():N}.tmp";

            using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, profile, FileManagerJsonContext.Default.Profile);
                stream.Flush(flushToDisk: true);   // §9 fsync point: flush before the rename
            }
            File.Move(tempPath, finalPath, overwrite: true);

            logger.LogInformation("Saved profile {ProfileId} ({Name})", profile.Id, profile.Name);
            return Result<IReadOnlyList<ValidationIssue>, string>.Success(issues);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not save profile {ProfileId} ({Name})", profile.Id, profile.Name);
            TryDeleteTemp(tempPath);
            return $"could not save profile {profile.Id}: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Saving profile {ProfileId} ({Name}) failed unexpectedly", profile.Id, profile.Name);
            TryDeleteTemp(tempPath);
            return $"could not save profile {profile.Id}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void TryDeleteTemp(string? tempPath)
    {
        if (tempPath is null)
            return;
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup of a failed save's leftover — logged (directive), never fatal.
            logger.LogWarning(ex, "Could not delete leftover profile temp file {Path}", tempPath);
        }
    }

    public Result Delete(Guid profileId)
    {
        try
        {
            File.Delete(ProfileFilePath(profileId));   // idempotent: deleting a missing file is a no-op
            logger.LogInformation("Deleted profile {ProfileId}", profileId);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not delete profile {ProfileId}", profileId);
            return $"could not delete profile {profileId}: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: unexpected exceptions become logged failures, not faulted callers.
            logger.LogError(ex, "Deleting profile {ProfileId} failed unexpectedly", profileId);
            return $"could not delete profile {profileId}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private string ProfileFilePath(Guid profileId) =>
        Path.Combine(ProfilesDirectory, profileId.ToString("D") + ".json");
}
