using FileManager.Contracts.Primitives;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Platform.Windows.Tests;

public sealed class WindowsVolumeInfoProviderTests
{
    private readonly WindowsVolumeInfoProvider _provider = new(NullLogger<WindowsVolumeInfoProvider>.Instance);

    [Fact]
    public void Reports_positive_free_space_for_the_temp_directory()
    {
        Result<long, string> result = _provider.GetAvailableFreeBytes(Path.GetTempPath());
        Assert.True(result.TryGetValue(out long free));
        Assert.True(free > 0);
    }

    [Fact]
    public void Volume_key_is_the_drive_root()
    {
        Result<string, string> result = _provider.GetVolumeKey(Path.GetTempPath());
        Assert.True(result.TryGetValue(out string? key));
        Assert.Equal(Path.GetPathRoot(Path.GetTempPath())!.ToLowerInvariant().TrimEnd('\\'), key.TrimEnd('\\'));
    }

    [Fact]
    public void Local_temp_path_is_not_a_network_path()
    {
        Assert.False(_provider.IsNetworkPath(Path.GetTempPath()));
    }
}
