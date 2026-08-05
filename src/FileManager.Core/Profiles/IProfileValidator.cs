using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using System.Collections.Generic;

namespace FileManager.Core.Profiles;

public interface IProfileValidator
{
    /// <summary>
    /// Validates that the profile is valid.
    /// </summary>
    /// <param name="candidate"></param>
    /// <param name="otherActiveProfiles"></param>
    /// <returns></returns>
    IReadOnlyList<ValidationIssue> Validate(
        Profile candidate, IReadOnlyList<Profile> otherActiveProfiles);
}
