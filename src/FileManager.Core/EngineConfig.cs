using System.Collections.Generic;

namespace FileManager.Core;

public sealed record EngineConfig
{
    public int MaxWorkers { get; init; } = System.Environment.ProcessorCount;
    public long PreflightSafetyMarginBytes { get; init; } = 64L * 1024 * 1024;  // 64 MiB
    public string? TempRoot { get; init; }                     // default: <root>\work
    public IReadOnlyList<string>? ExecutableAllowlist { get; init; }  // null = any existing file (spec §9)
    public bool LaunchTrayOnStart { get; init; } = true;
    public long JournalRotateAtBytes { get; init; } = 4L * 1024 * 1024;         // 4 MiB
}
