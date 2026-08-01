using FileManager.Contracts.Settings;
using FileManager.UI.Services;

namespace FileManager.UI.Tests;

/// <summary>Runs against the real machine, so the assertions are about the SHAPE of what comes back, not
/// about which volumes this box happens to have. The one that matters is the key format: a picked drive is
/// worthless unless its key is the same string the engine derives from a scanned path.</summary>
public sealed class SystemDrivesTests
{
    private static readonly IReadOnlyList<DriveOption> Drives = new SystemDrives().List();

    [Fact]
    public void At_least_one_drive_is_reported_on_a_machine_that_is_running_the_tests()
    {
        Assert.NotEmpty(Drives);
    }

    [Fact]
    public void Every_key_is_already_in_canonical_form()
    {
        foreach (DriveOption drive in Drives)
        {
            Assert.Equal(VolumeKeys.Normalize(drive.VolumeKey), drive.VolumeKey);
            Assert.DoesNotContain('\\', drive.VolumeKey);
            Assert.Equal(drive.VolumeKey.ToLowerInvariant(), drive.VolumeKey);
        }
    }

    [Fact]
    public void Every_label_names_the_drive_and_its_class()
    {
        foreach (DriveOption drive in Drives)
        {
            Assert.False(string.IsNullOrWhiteSpace(drive.Label));
            Assert.Contains(drive.VolumeKey.ToUpperInvariant(), drive.Label);
            Assert.Contains("(", drive.Label);      // the drive-class suffix
        }
    }

    [Fact]
    public void Keys_are_unique()
    {
        Assert.Equal(Drives.Count, Drives.Select(d => d.VolumeKey).Distinct().Count());
    }
}
