using FileManager.Contracts.Settings;
using FileManager.UI.Services;

namespace FileManager.UI.Tests;

/// <summary>The UI-owned settings file: the one the app can always read and write, including while the
/// service is unreachable. Every case here is a "must not lose the user's data" case — a failure mode
/// is a silently reset preference, so the store never throws and never half-writes.</summary>
public sealed class ClientSettingsStoreTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Write_then_Read_round_trips_every_field()
    {
        string dir = NewDir();
        try
        {
            string file = Path.Combine(dir, "client-settings.json");
            ClientSettings written = new(@"D:\tools\FileManager.Service.exe", ThemeMode.Dark,
                SidebarCollapsed: true, SidebarWidth: 321);

            ClientSettingsStore.Write(file, written);

            Assert.Equal(written, ClientSettingsStore.Read(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_absent_override_stays_absent_in_the_file()
    {
        // Blank means "probe the default location", and the file should say that by omission rather
        // than by storing an empty string that later reads as a configured-but-empty path.
        string dir = NewDir();
        try
        {
            string file = Path.Combine(dir, "client-settings.json");

            ClientSettingsStore.Write(file, ClientSettings.Default);

            Assert.DoesNotContain("ServiceExecutablePath", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.Null(ClientSettingsStore.Read(file).ServiceExecutablePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_theme_is_stored_by_name()
    {
        string dir = NewDir();
        try
        {
            string file = Path.Combine(dir, "client-settings.json");

            ClientSettingsStore.Write(file, ClientSettings.Default with { ThemeMode = ThemeMode.Dark });

            Assert.Contains("\"Dark\"", File.ReadAllText(file), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_returns_defaults_when_the_file_is_missing()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N"), "client-settings.json");

        Assert.Equal(ClientSettings.Default, ClientSettingsStore.Read(missing));
    }

    [Fact]
    public void Read_returns_defaults_when_the_file_is_corrupt()
    {
        string dir = NewDir();
        try
        {
            string file = Path.Combine(dir, "client-settings.json");
            File.WriteAllText(file, "{ this is not valid json");

            Assert.Equal(ClientSettings.Default, ClientSettingsStore.Read(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_creates_the_directory_when_it_does_not_exist()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "nested", "client-settings.json");

            ClientSettingsStore.Write(file, ClientSettings.Default with { SidebarWidth = 404 });

            Assert.Equal(404, ClientSettingsStore.Read(file).SidebarWidth);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_read_modify_write_leaves_the_other_writers_half_alone()
    {
        // The contract the sidebar and the settings window both depend on: two features share this file
        // and save on unrelated triggers, so a writer that rebuilt the record would erase the other.
        string dir = NewDir();
        try
        {
            string file = Path.Combine(dir, "client-settings.json");
            ClientSettingsStore.Write(file, ClientSettings.Default with
            {
                ServiceExecutablePath = @"D:\tools\FileManager.Service.exe",
            });

            // ...now the sidebar saves its layout, knowing nothing about the exe path.
            ClientSettingsStore.Write(file, ClientSettingsStore.Read(file) with { SidebarWidth = 500 });

            ClientSettings persisted = ClientSettingsStore.Read(file);
            Assert.Equal(500, persisted.SidebarWidth);
            Assert.Equal(@"D:\tools\FileManager.Service.exe", persisted.ServiceExecutablePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ============================ Migration off the pre-split files ============================

    [Fact]
    public void A_first_run_recovers_the_sidebar_layout_from_the_old_ui_state_file()
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ui-state.json"),
                """{ "SidebarCollapsed": true, "SidebarWidth": 321 }""");

            ClientSettings migrated = ClientSettingsStore.Read(Path.Combine(dir, "client-settings.json"));

            Assert.True(migrated.SidebarCollapsed);
            Assert.Equal(321, migrated.SidebarWidth);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_first_run_recovers_the_theme_from_the_services_settings_file()
    {
        // ThemeMode used to be a GlobalSettings member; without this the split would silently reset
        // every existing user's theme.
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{ "SchemaVersion": 4, "ThemeMode": "Dark" }""");

            Assert.Equal(ThemeMode.Dark,
                ClientSettingsStore.Read(Path.Combine(dir, "client-settings.json")).ThemeMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Migration_ignores_legacy_files_that_are_corrupt()
    {
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ui-state.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json either");

            Assert.Equal(ClientSettings.Default,
                ClientSettingsStore.Read(Path.Combine(dir, "client-settings.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_client_file_wins_over_the_legacy_files_once_it_exists()
    {
        // Migration is a first-run affair: once the client file is written it is authoritative, or a
        // stale settings.json would keep resurrecting the old theme on every launch.
        string dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "ThemeMode": "Dark" }""");
            string file = Path.Combine(dir, "client-settings.json");
            ClientSettingsStore.Write(file, ClientSettings.Default with { ThemeMode = ThemeMode.Light });

            Assert.Equal(ThemeMode.Light, ClientSettingsStore.Read(file).ThemeMode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
