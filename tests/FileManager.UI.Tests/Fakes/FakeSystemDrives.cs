using FileManager.UI.Services;

namespace FileManager.UI.Tests.Fakes;

/// <summary>A fixed drive list, so the settings drive picker is assertable without depending on whatever
/// volumes the test machine happens to have. Keys are in the canonical form <c>VolumeKeys.Normalize</c>
/// produces — the whole point of the picker is that they match.</summary>
internal sealed class FakeSystemDrives(params DriveOption[] drives) : ISystemDrives
{
    public static readonly DriveOption C = new("c:", "C: — Windows (Fixed)");
    public static readonly DriveOption D = new("d:", "D: — Data (Fixed)");

    private readonly IReadOnlyList<DriveOption> _drives = drives.Length > 0 ? drives : [C, D];

    public IReadOnlyList<DriveOption> List() => _drives;
}
