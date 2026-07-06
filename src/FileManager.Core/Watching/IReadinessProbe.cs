using FileManager.Core.Primitives;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Watching;

public interface IReadinessProbe
{
    Task<Result<bool, string>> IsReadyAsync(
        string path, ReadinessOptions options, CancellationToken ct = default);
}

public sealed record ReadinessOptions
{
    public required int StabilityIntervalMs { get; init; }
    public required bool IsNetworkPath { get; init; }
}
