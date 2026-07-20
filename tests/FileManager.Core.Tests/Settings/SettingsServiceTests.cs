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
