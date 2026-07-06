using System;
using System.Collections.Generic;

namespace FileManager.Core.Profiles;

public interface IProfileMatcher
{
    /// <summary>
    /// Finds all profiles with sources in the path.
    /// </summary>
    /// <param name="absolutePath"></param>
    /// <returns></returns>
    IReadOnlyList<ProfileMatch> FindMatches(string absolutePath);
}

public sealed record ProfileMatch(Guid ProfileId, string ProfileName, string MatchedSourceRoot);
