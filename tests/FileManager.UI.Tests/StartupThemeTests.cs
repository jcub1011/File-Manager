using FileManager.Contracts.Settings;
using FileManager.UI.Services;

namespace FileManager.UI.Tests;

/// <summary>Verifies <see cref="StartupTheme"/> reads the persisted theme straight from
/// client-settings.json so the correct variant can be applied before the first paint (no
/// light-to-dark startup flash) and with no running service.</summary>
public sealed class StartupThemeTests
{
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.System)]
    public void Read_returns_the_persisted_theme(ThemeMode mode)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "client-settings.json");
        try
        {
            ClientSettingsStore.Write(file, ClientSettings.Default with { ThemeMode = mode });

            Assert.Equal(mode, StartupTheme.Read(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_returns_System_when_the_file_is_missing()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "fm-theme-" + Guid.NewGuid().ToString("N"), "client-settings.json");

        Assert.Equal(ThemeMode.System, StartupTheme.Read(missing));
    }

    [Fact]
    public void Read_returns_System_when_the_file_is_corrupt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "client-settings.json");
        try
        {
            File.WriteAllText(file, "{ this is not valid json");

            Assert.Equal(ThemeMode.System, StartupTheme.Read(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
