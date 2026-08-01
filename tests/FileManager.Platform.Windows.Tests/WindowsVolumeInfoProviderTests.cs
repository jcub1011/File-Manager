using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
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
    public void Volume_key_is_the_drive_root_with_no_trailing_separator()
    {
        // Asserted exactly, not with a defensive TrimEnd on both sides: the trailing separator is the
        // whole point. GetPathRoot returns "C:\" and Path.TrimEndingDirectorySeparator leaves a root
        // alone, so the key used to come back as "c:\" and never matched the "c:" a user types into a
        // specific-drive override.
        Result<string, string> result = _provider.GetVolumeKey(Path.GetTempPath());
        Assert.True(result.TryGetValue(out string? key));
        Assert.Equal(Path.GetPathRoot(Path.GetTempPath())!.ToLowerInvariant().TrimEnd('\\'), key);
        Assert.DoesNotContain('\\', key);
    }

    [Fact]
    public void Local_temp_path_is_not_a_network_path()
    {
        Assert.False(_provider.IsNetworkPath(Path.GetTempPath()));
    }

    [Fact]
    public void Local_temp_path_is_classified_as_a_fixed_drive()
    {
        Assert.Equal(FileManager.Contracts.Settings.DriveClass.Fixed, _provider.GetDriveClass(Path.GetTempPath()));
    }

    [Fact]
    public void Reports_plausible_capacity_and_cluster_for_the_temp_directory()
    {
        Result<VolumeCapacity, string> result = _provider.GetVolumeCapacity(Path.GetTempPath());
        Assert.True(result.TryGetValue(out VolumeCapacity cap));
        Assert.True(cap.TotalBytes > 0);
        Assert.True(cap.FreeBytes > 0);
        Assert.True(cap.FreeBytes <= cap.TotalBytes);
        Assert.True(cap.BytesPerCluster >= 1);      // cluster size, or 1 when the query is unsupported
    }

    [Fact]
    public void Capacity_query_fails_soft_for_a_bogus_unc_path()
    {
        Result<VolumeCapacity, string> result =
            _provider.GetVolumeCapacity(@"\\this-server-does-not-exist-42\share\dir");
        Assert.False(result.TryGetValue(out _));    // a failure value, not an exception
    }
}
