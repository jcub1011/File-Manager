using FileManager.Contracts.Profiles;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Contracts.Primitives;
using System.Collections.Generic;

namespace FileManager.Core.Watching;

public interface ISourceScanner
{
    IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null);
}
