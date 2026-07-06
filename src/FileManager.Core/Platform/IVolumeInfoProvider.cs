using FileManager.Contracts.Primitives;

namespace FileManager.Core.Platform;

public interface IVolumeInfoProvider
{
    Result<long, string> GetAvailableFreeBytes(string path);

    /// <summary>Stable key grouping paths that share a volume (drive root or UNC share root).</summary>
    Result<string, string> GetVolumeKey(string path);

    bool IsNetworkPath(string path);
}
