using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

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
    public void Shell_vm_restores_the_persisted_sidebar_layout_and_saves_through_the_same_path()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-uistate-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "ui-state.json");
        try
        {
            UiStateStore.Write(file, new UiState(SidebarCollapsed: true, SidebarWidth: 321));

            MainWindowViewModel vm = new(
                new FakeIpcGateway(), new FakeFolderPicker(), new FakeLogFolder(), new FakeDryRunItemActions(),
                uiStatePath: file);

            Assert.True(vm.SidebarCollapsed);
            Assert.Equal(321, vm.SidebarExpandedWidth);

            vm.SidebarCollapsed = false;
            vm.SidebarExpandedWidth = 400;
            vm.SaveSidebarState();

            UiState persisted = UiStateStore.Read(file);
            Assert.False(persisted.SidebarCollapsed);
            Assert.Equal(400, persisted.SidebarWidth);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
