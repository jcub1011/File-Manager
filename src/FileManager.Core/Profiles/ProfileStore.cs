using FileManager.Contracts;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
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
public sealed class ProfileStore(ILogger<ProfileStore> logger, EnginePaths paths, IProfileValidator validator) : IProfileStore
{
    public Result<IReadOnlyList<Profile>, string> LoadAll()
    {
        try
        {
            if (!Directory.Exists(paths.ProfilesDirectory))
                return Array.Empty<Profile>();

            List<Profile> profiles = [];
            foreach (string file in Directory.EnumerateFiles(paths.ProfilesDirectory, "*.json"))
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
            }
            return profiles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not enumerate profiles: {ex.Message}";
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
            return $"could not load profile {profileId}: {ex.Message}";
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

        try
        {
            Directory.CreateDirectory(paths.ProfilesDirectory);
            string finalPath = ProfileFilePath(profile.Id);
            string tempPath = finalPath + ".tmp";

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
            return $"could not save profile {profile.Id}: {ex.Message}";
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
            return $"could not delete profile {profileId}: {ex.Message}";
        }
    }

    private string ProfileFilePath(Guid profileId) =>
        Path.Combine(paths.ProfilesDirectory, profileId.ToString("D") + ".json");
}
