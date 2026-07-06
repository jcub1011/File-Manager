using FileManager.Contracts.Profiles;
using FileManager.Core.Primitives;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Profiles;

public interface IProfileCatalog
{
    IReadOnlyList<Profile> All { get; }
    IReadOnlyList<Profile> Active { get; }
    event Action Changed;
    Result Reload();
}
