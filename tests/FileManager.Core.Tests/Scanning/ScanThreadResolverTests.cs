using FileManager.Contracts.Settings;
using FileManager.Core.Scanning;

namespace FileManager.Core.Tests.Scanning;

/// <summary>The precedence and key-matching rules for per-drive scan-thread budgets. The key-matching
/// cases exist because the two sides of the lookup once disagreed: the engine derived "c:\" from a real
/// path while the settings UI saved the "c:" a user typed, so every drive-letter specific override was
/// silently dead. Both sides now go through <see cref="VolumeKeys.Normalize"/> — the stored dictionary
/// keys by <c>SettingsService</c> on save, the lookup argument here.</summary>
public sealed class ScanThreadResolverTests
{
    [Theory]
    [InlineData("c:")]
    [InlineData("C:")]
    [InlineData(@"C:\")]
    [InlineData(@" c:\ ")]
    public void A_specific_override_matches_however_the_queried_volume_key_is_spelled(string queriedKey)
    {
        // "c:" is the canonical stored form: what a user types into the settings UI and what
        // SettingsService writes to disk. The engine may ask with any spelling of the same volume.
        ScanThreadingSettings settings = new()
        {
            PerDriveDefault = ThreadBudget.Explicit(99),
            SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["c:"] = ThreadBudget.Explicit(3) },
        };

        Assert.Equal(3, ScanThreadResolver.ResolvePerDriveCap(settings, queriedKey, DriveClass.Fixed));
    }

    [Fact]
    public void A_unc_share_override_matches_case_insensitively()
    {
        ScanThreadingSettings settings = new()
        {
            PerDriveDefault = ThreadBudget.Explicit(99),
            SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { [@"\\server\share"] = ThreadBudget.Explicit(2) },
        };

        Assert.Equal(2, ScanThreadResolver.ResolvePerDriveCap(settings, @"\\SERVER\Share", DriveClass.Network));
    }

    [Fact]
    public void Precedence_runs_specific_drive_then_drive_type_then_per_drive_default()
    {
        ScanThreadingSettings settings = new()
        {
            MaxScanThreads = ThreadBudget.Explicit(64),
            PerDriveDefault = ThreadBudget.Explicit(9),
            DriveTypeOverrides = new Dictionary<DriveClass, ThreadBudget> { [DriveClass.Fixed] = ThreadBudget.Explicit(5) },
            SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["c:"] = ThreadBudget.Explicit(1) },
        };

        Assert.Equal(1, ScanThreadResolver.ResolvePerDriveCap(settings, "c:", DriveClass.Fixed));    // specific wins
        Assert.Equal(5, ScanThreadResolver.ResolvePerDriveCap(settings, "d:", DriveClass.Fixed));    // falls to the type
        Assert.Equal(9, ScanThreadResolver.ResolvePerDriveCap(settings, "e:", DriveClass.Optical));  // falls to the default
    }

    [Fact]
    public void A_per_drive_cap_never_exceeds_the_global_scan_ceiling()
    {
        ScanThreadingSettings settings = new()
        {
            MaxScanThreads = ThreadBudget.Explicit(4),
            SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["c:"] = ThreadBudget.Explicit(100) },
        };

        Assert.Equal(4, ScanThreadResolver.ResolvePerDriveCap(settings, "c:", DriveClass.Fixed));
    }
}
