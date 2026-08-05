using FileManager.Contracts.Profiles;
using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;

namespace FileManager.Core.Profiles;

public interface IProfileCatalog
{
    IReadOnlyList<Profile> All { get; }
    IReadOnlyList<Profile> Active { get; }
    IDisposable Subscribe(Action changeHandler);
    Result Reload();
}
