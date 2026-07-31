using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Undo;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;

namespace FileManager.UI.Tests;

/// <summary>The Settings window's undo/redo, its unsaved-changes flag, and the specific-drive picker.
/// <see cref="SettingsViewModelTests"/> keeps the load/save mapping; this file covers what the editing
/// session itself has to guarantee.</summary>
public sealed class SettingsViewModelUndoTests
{
    private static SettingsViewModel NewVm(FakeIpcGateway? gateway = null) =>
        new(gateway ?? new FakeIpcGateway(), new FakeFolderPicker(), new FakeSystemDrives());

    // ============================ Unsaved changes ============================

    [Fact]
    public async Task A_freshly_loaded_window_is_clean_with_nothing_to_undo()
    {
        SettingsViewModel vm = NewVm(new FakeIpcGateway
        {
            GetSettingsResult = new GlobalSettings { ThemeMode = ThemeMode.Dark, ScratchDirectory = @"D:\scratch" },
        });

        await vm.LoadAsync();

        Assert.False(vm.IsDirty);
        Assert.False(vm.History.CanUndo);
        Assert.Equal("Close", vm.CloseButtonText);
    }

    [Fact]
    public async Task A_failed_load_still_leaves_the_window_clean()
    {
        // The error path returns early, so only the finally runs — Reset has to live there or the window
        // opens claiming unsaved changes it never had.
        SettingsViewModel vm = NewVm(new FakeIpcGateway
        {
            GetSettingsResult = new FileManager.Contracts.IPC.IpcError("UNAVAILABLE", "service unreachable"),
        });

        await vm.LoadAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.IsDirty);
        Assert.False(vm.History.CanUndo);
    }

    [Fact]
    public async Task Editing_a_setting_renames_the_close_button()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();

        vm.Theme.Value = ThemeMode.Dark;

        Assert.True(vm.IsDirty);
        Assert.Equal("Discard changes", vm.CloseButtonText);
    }

    [Fact]
    public async Task Undoing_back_to_the_loaded_state_restores_the_close_button()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        vm.Theme.Value = ThemeMode.Dark;

        vm.History.UndoCommand.Execute(null);

        Assert.False(vm.IsDirty);
        Assert.Equal("Close", vm.CloseButtonText);
    }

    [Fact]
    public async Task Saving_clears_the_unsaved_changes_flag()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        vm.MaxScanThreads.Auto = false;
        Assert.True(vm.IsDirty);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Equal("Close", vm.CloseButtonText);
        Assert.True(vm.History.CanUndo);            // still steppable, just no longer unsaved
    }

    [Fact]
    public async Task Changing_the_profiles_directory_does_not_make_the_window_dirty()
    {
        // The relocation already happened on disk — there is nothing pending to save and nothing to undo.
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new FileManager.Contracts.IPC.RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\moved"), new FakeSystemDrives())
        {
            ConfirmMoveProfiles = _ => Task.FromResult(false),
        };
        await vm.LoadAsync();

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\moved", vm.ProfilesDirectory.Value);
        Assert.False(vm.IsDirty);
        Assert.False(vm.History.CanUndo);
    }

    // ============================ Undo across the catalog ============================

    [Fact]
    public async Task Undo_reverts_a_theme_change()
    {
        SettingsViewModel vm = NewVm(new FakeIpcGateway
        {
            GetSettingsResult = new GlobalSettings { ThemeMode = ThemeMode.Light },
        });
        await vm.LoadAsync();

        vm.Theme.Value = ThemeMode.Dark;
        vm.History.UndoCommand.Execute(null);

        Assert.Equal(ThemeMode.Light, vm.Theme.Value);

        vm.History.RedoCommand.Execute(null);
        Assert.Equal(ThemeMode.Dark, vm.Theme.Value);
    }

    [Fact]
    public async Task Undo_reverts_an_auto_toggle_and_a_pinned_count_separately()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();

        vm.MaxScanThreads.Auto = false;
        vm.MaxScanThreads.Value = 12;

        vm.History.UndoCommand.Execute(null);
        Assert.False(vm.MaxScanThreads.Auto);         // the pin is undone...
        vm.History.UndoCommand.Execute(null);
        Assert.True(vm.MaxScanThreads.Auto);          // ...then the toggle
    }

    [Fact]
    public async Task Typing_a_scratch_path_is_a_single_undo_step()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        string original = vm.ScratchDirectory.Value;

        foreach (string typed in new[] { "D", "D:", @"D:\", @"D:\spill" })
            vm.ScratchDirectory.Value = typed;

        vm.History.UndoCommand.Execute(null);
        Assert.Equal(original, vm.ScratchDirectory.Value);
        Assert.False(vm.History.CanUndo);
    }

    [Fact]
    public async Task Undo_removes_an_added_specific_drive_row()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();

        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        SpecificDriveOverrideRowViewModel added = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);

        vm.History.UndoCommand.Execute(null);
        Assert.Empty(vm.DriveOverrides.SpecificDriveOverrides);

        vm.History.RedoCommand.Execute(null);
        Assert.Same(added, Assert.Single(vm.DriveOverrides.SpecificDriveOverrides));
    }

    [Fact]
    public async Task Undo_restores_a_removed_drive_type_row()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        vm.DriveOverrides.AddDriveTypeOverrideCommand.Execute(null);
        DriveTypeOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.DriveTypeOverrides);
        row.Class = DriveClass.Network;
        vm.DriveOverrides.RemoveDriveTypeOverrideCommand.Execute(row);

        vm.History.UndoCommand.Execute(null);

        DriveTypeOverrideRowViewModel restored = Assert.Single(vm.DriveOverrides.DriveTypeOverrides);
        Assert.Same(row, restored);
        Assert.Equal(DriveClass.Network, restored.Class);      // and with the edit it carried
    }

    [Fact]
    public async Task Loading_rows_is_not_recorded_as_editing_them()
    {
        SettingsViewModel vm = NewVm(new FakeIpcGateway
        {
            GetSettingsResult = new GlobalSettings
            {
                ScanThreading = new ScanThreadingSettings
                {
                    SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["c:"] = ThreadBudget.Explicit(2) },
                },
            },
        });

        await vm.LoadAsync();

        Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);
        Assert.False(vm.History.CanUndo);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Editing_a_loaded_row_is_undoable()
    {
        SettingsViewModel vm = NewVm(new FakeIpcGateway
        {
            GetSettingsResult = new GlobalSettings
            {
                ScanThreading = new ScanThreadingSettings
                {
                    SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["c:"] = ThreadBudget.Explicit(2) },
                },
            },
        });
        await vm.LoadAsync();
        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);

        row.Value = 8;
        vm.History.UndoCommand.Execute(null);

        Assert.Equal(2, row.Value);
    }

    [Fact]
    public async Task Every_declared_undoable_property_round_trips_through_its_own_accessors()
    {
        // Guards a copy-paste slip in an UndoableProperty declaration (a getter reading one property while
        // the setter writes another): reading a value and writing it straight back must be a no-op.
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();

        foreach (SettingsCategoryViewModel category in vm.Categories)
        {
            foreach (SettingItemViewModel item in category.Items)
            {
                if (item is not IUndoTrackable trackable)
                    continue;
                foreach (UndoableProperty property in trackable.UndoableProperties)
                {
                    object? before = property.Get();
                    property.Set(before);
                    Assert.True(
                        property.AreEqual(before, property.Get()),
                        $"\"{item.Id}\".{property.Name} does not read back what was written to it.");
                }
            }
        }

        Assert.False(vm.IsDirty);       // and writing the same values back is not an edit
    }

    // ============================ The drive picker ============================

    [Fact]
    public async Task A_new_row_offers_the_machines_drives()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();

        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);

        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);
        Assert.Equal(new[] { FakeSystemDrives.C, FakeSystemDrives.D }, row.AvailableDrives);
        Assert.Null(row.SelectedDrive);          // nothing picked yet, so the combo shows its placeholder
    }

    [Fact]
    public async Task Picking_a_drive_fills_the_volume_key_in_one_undo_step()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);
        vm.History.Reset();

        row.SelectedDrive = FakeSystemDrives.D;

        Assert.Equal("d:", row.VolumeKey);
        Assert.Equal(1, vm.History.UndoDepth);   // the key, not the key plus the selection
    }

    [Fact]
    public async Task Typing_a_key_that_names_a_real_drive_selects_it_in_the_picker()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);

        row.VolumeKey = @"C:\";                  // any spelling of the same volume

        Assert.Equal(FakeSystemDrives.C, row.SelectedDrive);
    }

    [Fact]
    public async Task A_unc_key_leaves_the_picker_empty_without_touching_the_key()
    {
        SettingsViewModel vm = NewVm();
        await vm.LoadAsync();
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);

        row.VolumeKey = @"\\server\share";

        Assert.Null(row.SelectedDrive);
        Assert.Equal(@"\\server\share", row.VolumeKey);
    }

    [Fact]
    public async Task A_picked_drive_saves_as_the_key_the_engine_looks_up()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), new FakeSystemDrives());
        await vm.LoadAsync();
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);
        row.SelectedDrive = FakeSystemDrives.C;
        row.Auto = false;
        row.Value = 3;

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        // "c:" is exactly what VolumeKeys.Normalize derives from a real path root, so this override
        // actually matches the C: volume at scan time.
        Assert.Equal(3, sent.ScanThreading.SpecificDriveOverrides["c:"].Value);
    }

    [Fact]
    public async Task A_drive_root_typed_with_its_separator_saves_as_the_canonical_key()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), new FakeSystemDrives());
        await vm.LoadAsync();
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        SpecificDriveOverrideRowViewModel row = Assert.Single(vm.DriveOverrides.SpecificDriveOverrides);
        row.VolumeKey = @"D:\";
        row.Auto = false;
        row.Value = 4;

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.True(sent.ScanThreading.SpecificDriveOverrides.ContainsKey("d:"));
        Assert.False(sent.ScanThreading.SpecificDriveOverrides.ContainsKey(@"d:\"));
    }

    [Fact]
    public async Task Two_spellings_of_the_same_drive_are_still_rejected_as_duplicates()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), new FakeSystemDrives());
        await vm.LoadAsync();
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        vm.DriveOverrides.AddSpecificDriveOverrideCommand.Execute(null);
        vm.DriveOverrides.SpecificDriveOverrides[0].VolumeKey = "c:";
        vm.DriveOverrides.SpecificDriveOverrides[1].VolumeKey = @"C:\";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);
        Assert.Contains("Duplicate", vm.ErrorMessage);
    }
}
