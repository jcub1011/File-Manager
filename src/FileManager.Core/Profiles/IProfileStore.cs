using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Profiles;

public interface IProfileStore
{
    /// <summary>
    /// Gets all the stored profiles.
    /// </summary>
    /// <returns></returns>
    Result<IReadOnlyList<Profile>, string> LoadAll();

    /// <summary>
    /// Loads a profile by id.
    /// </summary>
    /// <param name="profileId"></param>
    /// <returns></returns>
    Result<Profile, string> Load(Guid profileId);

    /// <summary>
    /// Saves a profile. Fails on any error or warning. Warnings don't block when <i>acknowledgeWarnings</i> is true.
    /// </summary>
    /// <param name="profile"></param>
    /// <param name="acknowledgeWarnings"></param>
    /// <returns></returns>
    Result<IReadOnlyList<ValidationIssue>, string> Save(Profile profile, bool acknowledgeWarnings);

    /// <summary>
    /// Deletes the profile by id.
    /// </summary>
    /// <param name="profileId"></param>
    /// <returns></returns>
    Result Delete(Guid profileId);
}
