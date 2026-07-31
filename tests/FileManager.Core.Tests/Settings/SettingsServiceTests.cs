using FileManager.Contracts.Settings;
using FileManager.Core;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Generic;

namespace FileManager.Core.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root;
    private readonly EnginePaths _paths;

    public SettingsServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new EnginePaths { Root = _root };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SettingsService NewService() => new(NullLogger<SettingsService>.Instance, _paths);

    [Fact]
    public void Absent_file_yields_defaults()
    {
        Assert.Equal(GlobalSettings.Default, NewService().Current);
    }

    [Fact]
    public void Update_persists_and_a_fresh_instance_reloads_it()
    {
        SettingsService service = NewService();
        var result = service.Update(new GlobalSettings
        {
            ScanThreading = new ScanThreadingSettings { MaxScanThreads = ThreadBudget.Explicit(6) },
        });

        Assert.True(result.TryGetValue(out GlobalSettings? saved));
        Assert.Equal(6, saved!.ScanThreading.MaxScanThreads.Value);
        Assert.True(File.Exists(_paths.SettingsFilePath));

        GlobalSettings reloaded = NewService().Current;
        Assert.Equal(6, reloaded.ScanThreading.MaxScanThreads.Value);
    }

    [Fact]
    public void Update_clamps_an_explicit_value_below_one()
    {
        SettingsService service = NewService();
        service.Update(new GlobalSettings
        {
            ScanThreading = new ScanThreadingSettings { MaxScanThreads = ThreadBudget.Explicit(0) },
        });
        Assert.Equal(1, service.Current.ScanThreading.MaxScanThreads.Value);
    }

    [Fact]
    public void Update_normalizes_and_clamps_per_drive_overrides()
    {
        SettingsService service = NewService();
        service.Update(new GlobalSettings
        {
            ScanThreading = new ScanThreadingSettings
            {
                SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["  C:  "] = ThreadBudget.Explicit(0) },
            },
        });

        var overrides = service.Current.ScanThreading.SpecificDriveOverrides;
        Assert.True(overrides.ContainsKey("c:"));   // trimmed + lower-cased to the volume-key form
        Assert.Equal(1, overrides["c:"].Value);      // explicit value clamped to >= 1
    }

    [Fact]
    public void Update_strips_a_trailing_separator_from_a_specific_drive_key()
    {
        // A drive root spelled the way the OS hands it back ("C:\") has to collapse onto the canonical
        // "c:" the engine looks up with, or the override matches nothing.
        SettingsService service = NewService();
        service.Update(new GlobalSettings
        {
            ScanThreading = new ScanThreadingSettings
            {
                SpecificDriveOverrides = new Dictionary<string, ThreadBudget>
                {
                    [@"C:\"] = ThreadBudget.Explicit(3),
                    [@"\\Server\Share\"] = ThreadBudget.Explicit(2),
                },
            },
        });

        var overrides = service.Current.ScanThreading.SpecificDriveOverrides;
        Assert.Equal(3, overrides["c:"].Value);
        Assert.Equal(2, overrides[@"\\server\share"].Value);
    }

    [Fact]
    public void Legacy_manual_worker_pin_migrates_to_max_hash_threads()
    {
        // A pre-v2 file: scalar dry-run concurrency, no ScanThreading member.
        File.WriteAllText(_paths.SettingsFilePath,
            """
            {"SchemaVersion":1,"ServiceStartupMode":"StartAndStopWithProgram","DryRunConcurrencyMode":"Manual","DryRunManualWorkers":6,"ThemeMode":"System"}
            """);

        GlobalSettings loaded = NewService().Current;

        Assert.Equal(6, loaded.ScanThreading.MaxHashThreads.Value);   // pin carried into the hash phase
        Assert.True(loaded.ScanThreading.MaxScanThreads.IsAuto);      // untouched levels stay auto
    }

    [Fact]
    public void A_v2_file_without_a_legacy_pin_is_not_migrated()
    {
        NewService().Update(new GlobalSettings());   // writes a canonical v2 file (ScanThreading present)
        Assert.True(NewService().Current.ScanThreading.MaxHashThreads.IsAuto);
    }

    [Fact]
    public void Scan_threading_equality_is_structural_over_the_override_maps()
    {
        ScanThreadingSettings a = new()
        {
            DriveTypeOverrides = new Dictionary<DriveClass, ThreadBudget> { [DriveClass.Network] = ThreadBudget.Explicit(2) },
        };
        ScanThreadingSettings b = new()
        {
            DriveTypeOverrides = new Dictionary<DriveClass, ThreadBudget> { [DriveClass.Network] = ThreadBudget.Explicit(2) },
        };

        Assert.Equal(a, b);   // distinct dictionary instances, equal contents
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // An explicit default collapses so it compares equal to the absent-field default.
        Assert.Equal(GlobalSettings.Default, new GlobalSettings { ScanThreading = ScanThreadingSettings.Default });
    }

    [Fact]
    public void Default_scratch_directory_is_anchored_at_local_app_data_not_the_cwd()
    {
        // The service's cwd is Program Files (unwritable) or System32 (Run-key launch); the default
        // must never derive from it.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, GlobalSettings.DefaultScratchDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(localAppData, GlobalSettings.DefaultProfilesDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unset_directories_stay_absent_through_an_update_round_trip()
    {
        // Saving defaults must NOT pin the resolved default paths into settings.json — a pinned
        // absolute default would stick forever even if the machine's default location changes.
        NewService().Update(new GlobalSettings());

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_paths.SettingsFilePath));
        foreach (string key in new[] { "ScratchDirectory", "ProfilesDirectory" })
            Assert.True(
                !doc.RootElement.TryGetProperty(key, out var el) || el.ValueKind == System.Text.Json.JsonValueKind.Null,
                $"{key} must stay absent (null) in settings.json when unset");

        Assert.Equal(GlobalSettings.Default, NewService().Current);
    }

    [Fact]
    public void Explicit_directories_round_trip_and_a_default_valued_write_collapses()
    {
        string scratch = Path.Combine(_root, "my-scratch");
        NewService().Update(new GlobalSettings { ScratchDirectory = scratch, ProfilesDirectory = Path.Combine(_root, "my-profiles") });

        GlobalSettings reloaded = NewService().Current;
        Assert.Equal(scratch, reloaded.ScratchDirectory);
        Assert.Equal(Path.Combine(_root, "my-profiles"), reloaded.ProfilesDirectory);

        // Writing the default value (even with different casing) collapses back to absent.
        NewService().Update(new GlobalSettings { ScratchDirectory = GlobalSettings.DefaultScratchDirectory.ToUpperInvariant() });
        Assert.Equal(GlobalSettings.Default.ScratchDirectory, NewService().Current.ScratchDirectory);
    }

    [Fact]
    public void Default_startup_mode_is_start_and_stop_with_program()
    {
        Assert.Equal(ServiceStartupMode.StartAndStopWithProgram, NewService().Current.ServiceStartupMode);
    }

    [Fact]
    public void Update_persists_and_reloads_the_startup_mode()
    {
        NewService().Update(new GlobalSettings { ServiceStartupMode = ServiceStartupMode.RunOnStartup });
        Assert.Equal(ServiceStartupMode.RunOnStartup, NewService().Current.ServiceStartupMode);
    }

    [Fact]
    public void Default_theme_mode_is_system()
    {
        Assert.Equal(ThemeMode.System, NewService().Current.ThemeMode);
    }

    [Fact]
    public void Update_persists_and_reloads_the_theme_mode()
    {
        NewService().Update(new GlobalSettings { ThemeMode = ThemeMode.Dark });
        Assert.Equal(ThemeMode.Dark, NewService().Current.ThemeMode);
    }
}
