using FileManager.UI.Services;

namespace FileManager.UI.Tests;

/// <summary>Verifies the client-side sidebar layout store round-trips and falls back to defaults on a
/// missing or corrupt file (see <see cref="UiStateStore"/>).</summary>
public sealed class UiStateStoreTests
{
    [Fact]
    public void Write_then_Read_round_trips_the_state()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-uistate-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "ui-state.json");
        try
        {
            UiState written = new(SidebarCollapsed: true, SidebarWidth: 321);
            UiStateStore.Write(file, written);

            UiState read = UiStateStore.Read(file);

            Assert.True(read.SidebarCollapsed);
            Assert.Equal(321, read.SidebarWidth);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_returns_defaults_when_the_file_is_missing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "fm-uistate-" + Guid.NewGuid().ToString("N"), "ui-state.json");

        Assert.Equal(UiState.Default, UiStateStore.Read(missing));
    }

    [Fact]
    public void Read_returns_defaults_when_the_file_is_corrupt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-uistate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "ui-state.json");
        try
        {
            File.WriteAllText(file, "{ this is not valid json");

            Assert.Equal(UiState.Default, UiStateStore.Read(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
