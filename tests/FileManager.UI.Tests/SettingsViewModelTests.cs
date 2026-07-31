using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;

namespace FileManager.UI.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Load_reflects_an_explicit_backend_scan_thread_pin()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings
            {
                ScanThreading = new ScanThreadingSettings { MaxScanThreads = ThreadBudget.Explicit(4) },
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

        await vm.LoadAsync();

        Assert.False(vm.MaxScanThreads.Auto);
        Assert.Equal(4, vm.MaxScanThreads.Value);
        Assert.True(vm.MaxScanThreads.ShowValue);
    }

    [Fact]
    public async Task Save_sends_an_explicit_scan_thread_count()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        vm.MaxScanThreads.Auto = false;
        vm.MaxScanThreads.Value = 7;

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(7, sent.ScanThreading.MaxScanThreads.Value);
    }

    [Fact]
    public async Task Save_sends_auto_when_checked()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        vm.MaxScanThreads.Auto = true;
        vm.MaxScanThreads.Value = 7;

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.True(sent.ScanThreading.MaxScanThreads.IsAuto);
    }

    [Fact]
    public async Task Save_rejects_duplicate_drive_type_overrides()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        vm.DriveOverrides.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = DriveClass.Network, Value = 2 });
        vm.DriveOverrides.DriveTypeOverrides.Add(new DriveTypeOverrideRowViewModel { Class = DriveClass.Network, Value = 4 });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);            // save aborted, no data silently dropped
        Assert.Contains("Duplicate", vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_rejects_specific_drive_keys_that_collide_after_normalization()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        vm.DriveOverrides.SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = "C:", Value = 2 });
        vm.DriveOverrides.SpecificDriveOverrides.Add(new SpecificDriveOverrideRowViewModel { VolumeKey = " c: ", Value = 4 });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);
        Assert.Contains("Duplicate", vm.ErrorMessage);
    }

    [Fact]
    public async Task A_successful_save_reports_it_and_leaves_the_window_open()
    {
        // Saving commits the edits; it does not decide the user is finished. Only Close and the
        // title-bar X close the window, so the view model has no close seam at all.
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal("Saved.", vm.StatusMessage);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Discarding_changes_puts_every_setting_back_and_leaves_the_window_open()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ScratchDirectory = @"D:\stored" },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        await vm.LoadAsync();

        vm.ScratchDirectory.Value = @"E:\edited";
        vm.MaxScanThreads.Auto = false;
        Assert.True(vm.IsDirty);

        await vm.DiscardChangesCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\stored", vm.ScratchDirectory.Value);
        Assert.False(vm.IsDirty);
        Assert.False(vm.History.CanUndo);                  // discarding leaves nothing to step back through
        Assert.Empty(gateway.SaveSettingsCalls);           // ...and nothing was written
        Assert.Equal("Changes discarded.", vm.StatusMessage);
    }

    [Fact]
    public async Task Discard_is_only_offered_while_there_is_something_to_discard()
    {
        SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(),
            clientSettingsPath: TempFiles.ClientSettings());
        await vm.LoadAsync();

        Assert.False(vm.DiscardChangesCommand.CanExecute(null));

        vm.MaxScanThreads.Auto = false;
        Assert.True(vm.DiscardChangesCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_close_button_names_the_consequence_while_edits_are_pending()
    {
        // Close still discards, so it still has to say so — the Discard button beside it is the
        // non-destructive alternative, not a replacement for the warning.
        SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(),
            clientSettingsPath: TempFiles.ClientSettings());
        await vm.LoadAsync();

        Assert.Equal("Close", vm.CloseButtonText);

        vm.MaxScanThreads.Auto = false;
        Assert.Equal("Close without saving", vm.CloseButtonText);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Close", vm.CloseButtonText);
    }

    [Fact]
    public async Task Load_and_save_round_trip_the_theme_mode()
    {
        // The theme is client-side: it round-trips through client-settings.json, never over IPC.
        string clientFile = TempFiles.ClientSettings();
        ClientSettingsStore.Write(clientFile, ClientSettings.Default with { ThemeMode = ThemeMode.Dark });
        SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);

        await vm.LoadAsync();
        Assert.Equal(ThemeMode.Dark, vm.Theme.Value);

        vm.Theme.Value = ThemeMode.Light;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ThemeMode.Light, ClientSettingsStore.Read(clientFile).ThemeMode);
    }

    [Fact]
    public async Task Load_reflects_the_profiles_directory()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ProfilesDirectory = @"D:\profiles" },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

        await vm.LoadAsync();

        Assert.Equal(@"D:\profiles", vm.ProfilesDirectory.Value);
    }

    [Fact]
    public async Task Save_carries_the_profiles_directory_through_unchanged()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ProfilesDirectory = @"D:\profiles" },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        await vm.LoadAsync();

        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(@"D:\profiles", sent.ProfilesDirectory);
    }

    [Fact]
    public async Task Change_profiles_directory_relocates_with_the_move_choice_and_updates_the_path()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
                MovedCount = 1,
            },
        };
        FakeFolderPicker picker = new(@"E:\moved");
        SettingsViewModel vm = new(gateway, picker, clientSettingsPath: TempFiles.ClientSettings()) { ConfirmMoveProfiles = _ => Task.FromResult(true) };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        (string dir, bool move) = Assert.Single(gateway.RelocateCalls);
        Assert.Equal(@"E:\moved", dir);
        Assert.True(move);
        Assert.Equal(@"E:\moved", vm.ProfilesDirectory.Value);
    }

    [Fact]
    public async Task Change_profiles_directory_refreshes_the_profile_list_on_success()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
            },
        };
        bool refreshed = false;
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\moved"), clientSettingsPath: TempFiles.ClientSettings())
        {
            ConfirmMoveProfiles = _ => Task.FromResult(false),
            ProfilesRelocated = () => { refreshed = true; return Task.CompletedTask; },
        };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.True(refreshed);
    }

    [Fact]
    public async Task Change_profiles_directory_defaults_to_not_moving_when_declined()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\fresh" },
            },
        };
        FakeFolderPicker picker = new(@"E:\fresh");
        SettingsViewModel vm = new(gateway, picker, clientSettingsPath: TempFiles.ClientSettings()) { ConfirmMoveProfiles = _ => Task.FromResult(false) };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        (_, bool move) = Assert.Single(gateway.RelocateCalls);
        Assert.False(move);
    }

    [Fact]
    public async Task Change_profiles_directory_surfaces_skipped_collisions_instead_of_clean_success()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\moved" },
                MovedCount = 2,
                SkippedFiles = ["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.json"],
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\moved"), clientSettingsPath: TempFiles.ClientSettings())
        {
            ConfirmMoveProfiles = _ => Task.FromResult(true),
        };

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\moved", vm.ProfilesDirectory.Value);   // the switch itself succeeded
        Assert.Null(vm.StatusMessage);                           // ...but it is not reported as a clean success
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("stayed in the old folder", vm.ErrorMessage);
    }

    [Fact]
    public async Task Change_profiles_directory_survives_a_corrupt_empty_current_path()
    {
        FakeIpcGateway gateway = new()
        {
            RelocateResult = new RelocateProfilesResponse
            {
                Settings = new GlobalSettings { ProfilesDirectory = @"E:\recovered" },
            },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(@"E:\recovered"), clientSettingsPath: TempFiles.ClientSettings());
        vm.ProfilesDirectory.Value = "";

        // Corrupt settings hand the VM an empty current path; the command must relocate, not throw
        // out of the command boundary (Path.GetFullPath("") is an ArgumentException).
        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Single(gateway.RelocateCalls);
        Assert.Equal(@"E:\recovered", vm.ProfilesDirectory.Value);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Both_folder_pickers_open_at_the_currently_configured_folder()
    {
        // Re-choosing a folder should start where the setting already points, not at the OS default.
        FakeFolderPicker picker = new(result: null);
        SettingsViewModel vm = new(new FakeIpcGateway(), picker, clientSettingsPath: TempFiles.ClientSettings());
        vm.ScratchDirectory.Value = @"D:\scratch";
        vm.ProfilesDirectory.Value = @"E:\profiles";

        await vm.BrowseScratchDirectoryCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\scratch", picker.LastStartNear);

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);
        Assert.Equal(@"E:\profiles", picker.LastStartNear);
    }

    [Fact]
    public async Task Change_profiles_directory_does_nothing_when_the_picker_is_cancelled()
    {
        FakeIpcGateway gateway = new();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(result: null), clientSettingsPath: TempFiles.ClientSettings());

        await vm.ChangeProfilesDirectoryCommand.ExecuteAsync(null);

        Assert.Empty(gateway.RelocateCalls);
    }

    [Fact]
    public async Task Load_and_save_round_trip_the_startup_mode()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.RunOnStartup },
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

        await vm.LoadAsync();
        Assert.Equal(ServiceStartupMode.RunOnStartup, vm.StartupMode.Value);

        vm.StartupMode.Value = ServiceStartupMode.StartOnProgramOpen;
        await vm.SaveCommand.ExecuteAsync(null);

        GlobalSettings sent = Assert.Single(gateway.SaveSettingsCalls);
        Assert.Equal(ServiceStartupMode.StartOnProgramOpen, sent.ServiceStartupMode);
    }

    [Fact]
    public async Task Save_refuses_after_a_failed_load_rather_than_persisting_the_defaults()
    {
        // The dialog stays populated after a failed load (the constructor's defaults), so an
        // unguarded Save would rewrite settings.json with them — resetting a relocated
        // ProfilesDirectory and deleting every per-drive override.
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe"),
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

        await vm.LoadAsync();
        Assert.NotNull(vm.ErrorMessage);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(gateway.SaveSettingsCalls);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_is_allowed_again_once_a_load_succeeds()
    {
        FakeIpcGateway gateway = new()
        {
            GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe"),
        };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());
        await vm.LoadAsync();

        gateway.GetSettingsResult = new GlobalSettings { ServiceStartupMode = ServiceStartupMode.RunOnStartup };
        await vm.LoadAsync();
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Single(gateway.SaveSettingsCalls);
    }

    // ============================ Service executable path ============================

    /// <summary>A real file to point the setting at. The validation deliberately rejects a path with
    /// no file, so these tests need something that actually exists on disk.</summary>
    private static string RealExe(string dir, string name = "FileManager.Service.exe")
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "not really an executable");
        return path;
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Points FILEMANAGER_SERVICE_EXE somewhere real for the scope, so a fallback exists to
    /// be named. Restores whatever was there.</summary>
    private static IDisposable EnvOverride(string? value) => new EnvScope(value);

    private sealed class EnvScope : IDisposable
    {
        private readonly string? _original =
            Environment.GetEnvironmentVariable(ServiceLauncher.ServiceExeOverrideVariable);

        public EnvScope(string? value) =>
            Environment.SetEnvironmentVariable(ServiceLauncher.ServiceExeOverrideVariable, value);

        public void Dispose() =>
            Environment.SetEnvironmentVariable(ServiceLauncher.ServiceExeOverrideVariable, _original);
    }

    // ============================ Switching to a new service executable ============================

    private static EngineStatusSnapshot RunningFrom(string exe, int jobsInFlight = 0) =>
        new(false, 0, jobsInFlight, 0, null) { ExecutablePath = exe };

    /// <summary>A view model whose exe path starts at <paramref name="stored"/>, wired to record the
    /// confirmation prompt and answer it with <paramref name="stopPrevious"/>.</summary>
    private static (SettingsViewModel Vm, List<string> Prompts) SwitchVm(
        FakeIpcGateway gateway, string clientFile, string? stored, bool stopPrevious)
    {
        if (stored is not null)
            ClientSettingsStore.Write(clientFile, ClientSettings.Default with { ServiceExecutablePath = stored });

        List<string> prompts = [];
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: clientFile)
        {
            ConfirmStopPreviousService = message =>
            {
                prompts.Add(message);
                return Task.FromResult(stopPrevious);
            },
            // The give-up path polls; nothing here is real, so there is nothing to wait for.
            SwitchAttempts = 3,
            SwitchPollDelay = TimeSpan.Zero,
        };
        return (vm, prompts);
    }

    [Fact]
    public async Task Pointing_at_a_different_executable_offers_to_stop_the_running_one()
    {
        string dir = NewDir();
        try
        {
            string oldExe = RealExe(dir, "Old.exe");
            string newExe = RealExe(dir);
            FakeIpcGateway gateway = new()
            {
                StatusResult = RunningFrom(oldExe, jobsInFlight: 2),
                StatusAfterReset = RunningFrom(newExe),
            };
            (SettingsViewModel vm, List<string> prompts) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), oldExe, stopPrevious: true);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = newExe;
            await vm.SaveCommand.ExecuteAsync(null);

            string prompt = Assert.Single(prompts);
            Assert.Contains(oldExe, prompt);
            Assert.Contains(newExe, prompt);
            Assert.Contains("2 job(s)", prompt);            // stopping it would interrupt real work
            Assert.Equal(1, gateway.ShutdownCalls);
            Assert.True(gateway.ResetConnectionCalls > 0);  // ...and it reconnects to the new one
            Assert.Contains(newExe, vm.StatusMessage);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Declining_to_stop_the_running_service_leaves_it_alone_and_starts_nothing()
    {
        // "so that there aren't duplicate services running" — only one service can hold the pipe, so
        // launching a second while the first lives would be futile as well as unwanted.
        string dir = NewDir();
        try
        {
            string oldExe = RealExe(dir, "Old.exe");
            string newExe = RealExe(dir);
            FakeIpcGateway gateway = new() { StatusResult = RunningFrom(oldExe) };
            (SettingsViewModel vm, List<string> prompts) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), oldExe, stopPrevious: false);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = newExe;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Single(prompts);
            Assert.Equal(0, gateway.ShutdownCalls);
            Assert.Equal(0, gateway.ResetConnectionCalls);
            Assert.Contains("left alone", vm.StatusMessage);
            // The path is still saved — declining the restart is not declining the setting.
            Assert.Equal(newExe, ClientSettingsStore.Read(Path.Combine(dir, "client-settings.json")).ServiceExecutablePath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Saving_a_path_that_matches_the_running_service_prompts_nothing_and_restarts_nothing()
    {
        // The no-duplicates rule from the other side: the service already running IS the one being
        // asked for, so there is nothing to stop and nothing to launch.
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            FakeIpcGateway gateway = new() { StatusResult = RunningFrom(exe) };
            (SettingsViewModel vm, List<string> prompts) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), stored: null, stopPrevious: true);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = exe;      // newly set, but it is what is already running
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Empty(prompts);
            Assert.Equal(0, gateway.ShutdownCalls);
            Assert.Equal(0, gateway.ResetConnectionCalls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_save_that_does_not_touch_the_executable_path_never_prompts()
    {
        // "This should only trigger on save" — and only on a save that actually changed the path.
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir, "Old.exe");
            FakeIpcGateway gateway = new() { StatusResult = RunningFrom(exe) };
            (SettingsViewModel vm, List<string> prompts) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), exe, stopPrevious: true);
            await vm.LoadAsync();

            vm.MaxScanThreads.Auto = false;     // an unrelated edit
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Empty(prompts);
            Assert.Equal(0, gateway.ShutdownCalls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Changing_the_path_while_nothing_is_connected_just_resets_so_the_new_one_starts()
    {
        string dir = NewDir();
        try
        {
            string newExe = RealExe(dir);
            FakeIpcGateway gateway = new() { StatusResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe") };
            (SettingsViewModel vm, List<string> prompts) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), stored: null, stopPrevious: true);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = newExe;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Empty(prompts);                          // nothing running to ask about
            Assert.Equal(0, gateway.ShutdownCalls);
            Assert.True(gateway.ResetConnectionCalls > 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_switch_that_never_comes_up_is_reported()
    {
        // The new executable is stoppable and startable but never actually serves — the exact shape of
        // pointing the setting at some unrelated program.
        string dir = NewDir();
        try
        {
            string oldExe = RealExe(dir, "Old.exe");
            string newExe = RealExe(dir);
            FakeIpcGateway gateway = new() { StatusResult = RunningFrom(oldExe) };
            (SettingsViewModel vm, _) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), oldExe, stopPrevious: true);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = newExe;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(1, gateway.ShutdownCalls);
            Assert.Contains(newExe, vm.ErrorMessage);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_failed_shutdown_does_not_pretend_the_switch_happened()
    {
        string dir = NewDir();
        try
        {
            string oldExe = RealExe(dir, "Old.exe");
            string newExe = RealExe(dir);
            FakeIpcGateway gateway = new()
            {
                StatusResult = RunningFrom(oldExe),
                ShutdownResult = new IpcError("IPC_TRANSPORT", "refused"),
            };
            (SettingsViewModel vm, _) =
                SwitchVm(gateway, Path.Combine(dir, "client-settings.json"), oldExe, stopPrevious: true);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = newExe;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Contains("Could not stop", vm.ErrorMessage);
            Assert.Equal(0, gateway.ResetConnectionCalls);      // never reconnected to anything new
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ============================ The notice shown under the box ============================
    // Pure mapping, so every branch is pinned without touching a filesystem.

    private static ServiceExeResolution Resolution(params ServiceExeCandidate[] candidates) => new(candidates);

    [Fact]
    public void A_blank_path_reports_which_copy_will_actually_be_used()
    {
        (string text, SettingNoticeSeverity severity) = SettingsViewModel.DescribeServiceExe(
            Resolution(new ServiceExeCandidate(ServiceExeSource.BesideApp, @"C:\app\FileManager.Service.exe", true)));

        Assert.Equal(SettingNoticeSeverity.Info, severity);
        Assert.Contains(@"C:\app\FileManager.Service.exe", text);
    }

    [Fact]
    public void A_missing_configured_path_names_the_fallback_that_replaces_it()
    {
        (string text, SettingNoticeSeverity severity) = SettingsViewModel.DescribeServiceExe(
            Resolution(
                new ServiceExeCandidate(ServiceExeSource.Setting, @"D:\gone\FileManager.Service.exe", false),
                new ServiceExeCandidate(ServiceExeSource.BesideApp, @"C:\app\FileManager.Service.exe", true)));

        Assert.Equal(SettingNoticeSeverity.Warning, severity);
        Assert.Contains(@"C:\app\FileManager.Service.exe", text);
    }

    [Fact]
    public void A_missing_configured_path_with_no_fallback_is_an_error_listing_what_was_checked()
    {
        (string text, SettingNoticeSeverity severity) = SettingsViewModel.DescribeServiceExe(
            Resolution(
                new ServiceExeCandidate(ServiceExeSource.Setting, @"D:\gone\FileManager.Service.exe", false),
                new ServiceExeCandidate(ServiceExeSource.BesideApp, @"C:\app\FileManager.Service.exe", false)));

        Assert.Equal(SettingNoticeSeverity.Error, severity);
        Assert.Contains(@"D:\gone\FileManager.Service.exe", text);
        Assert.Contains(@"C:\app\FileManager.Service.exe", text);
    }

    [Fact]
    public void A_file_that_is_not_the_service_warns_that_it_will_never_connect()
    {
        // The failure that looks like nothing is wrong: it launches fine and simply never answers.
        (string text, SettingNoticeSeverity severity) = SettingsViewModel.DescribeServiceExe(
            Resolution(new ServiceExeCandidate(ServiceExeSource.Setting, @"D:\tools\Notepad.exe", true)));

        Assert.Equal(SettingNoticeSeverity.Warning, severity);
        Assert.Contains(ServiceLauncher.ServiceExeName, text);
    }

    [Fact]
    public void A_valid_configured_path_confirms_rather_than_warning()
    {
        (string text, SettingNoticeSeverity severity) = SettingsViewModel.DescribeServiceExe(
            Resolution(new ServiceExeCandidate(ServiceExeSource.Setting, @"D:\app\FileManager.Service.exe", true)));

        Assert.Equal(SettingNoticeSeverity.Info, severity);
        Assert.Contains(@"D:\app\FileManager.Service.exe", text);
    }

    [Fact]
    public async Task The_notice_updates_as_the_value_is_edited_without_saving()
    {
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(),
                clientSettingsPath: Path.Combine(dir, "client-settings.json"));
            await vm.LoadAsync();

            vm.ServiceExePath.Value = Path.Combine(dir, "nope", "FileManager.Service.exe");
            await WaitForNoticeAsync(vm, SettingNoticeSeverity.Error, SettingNoticeSeverity.Warning);

            vm.ServiceExePath.Value = exe;
            await WaitForNoticeAsync(vm, SettingNoticeSeverity.Info);
            Assert.Contains(exe, vm.ServiceExePath.Notice);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The edit-time refresh is fire-and-forget onto a worker thread (a half-typed UNC path
    /// must not freeze the window), so a test has to wait for it to land.</summary>
    private static async Task WaitForNoticeAsync(SettingsViewModel vm, params SettingNoticeSeverity[] expected)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (vm.ServiceExePath.HasNotice && expected.Contains(vm.ServiceExePath.NoticeSeverity))
                return;
            await Task.Delay(20);
        }
        Assert.Fail($"notice never became {string.Join(" or ", expected)}; " +
                    $"it is {vm.ServiceExePath.NoticeSeverity}: \"{vm.ServiceExePath.Notice}\"");
    }

    [Fact]
    public async Task Load_populates_the_service_exe_path_from_the_client_file()
    {
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            string clientFile = Path.Combine(dir, "client-settings.json");
            ClientSettingsStore.Write(clientFile, ClientSettings.Default with { ServiceExecutablePath = exe });
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);

            await vm.LoadAsync();

            Assert.Equal(exe, vm.ServiceExePath.Value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Save_persists_the_service_exe_path_even_when_the_settings_load_failed()
    {
        // THE point of the feature: a wrong path is why the service is unreachable, so the fix has to
        // be savable while it is unreachable. The GlobalSettings half must still be left untouched.
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            string clientFile = Path.Combine(dir, "client-settings.json");
            FakeIpcGateway gateway = new() { GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe") };
            SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = exe;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(exe, ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
            Assert.Empty(gateway.SaveSettingsCalls);
            Assert.NotNull(vm.StatusMessage);
            Assert.NotNull(vm.ErrorMessage);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_failed_load_disables_only_the_service_owned_settings()
    {
        FakeIpcGateway gateway = new() { GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe") };
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: TempFiles.ClientSettings());

        await vm.LoadAsync();

        Assert.True(vm.ServiceExePath.IsEnabled);
        Assert.True(vm.Theme.IsEnabled);
        Assert.False(vm.StartupMode.IsEnabled);
        Assert.False(vm.ScratchDirectory.IsEnabled);
        Assert.False(vm.MaxScanThreads.IsEnabled);
        Assert.False(vm.DriveOverrides.IsEnabled);
        // Greying without saying why is just a dead control.
        Assert.NotNull(vm.StartupMode.DisabledTooltip);
        Assert.Null(vm.ServiceExePath.DisabledTooltip);
    }

    [Fact]
    public async Task A_successful_load_leaves_every_setting_enabled()
    {
        SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(),
            clientSettingsPath: TempFiles.ClientSettings());

        await vm.LoadAsync();

        Assert.All(vm.Categories.SelectMany(c => c.Items), i => Assert.True(i.IsEnabled));
    }

    [Fact]
    public async Task A_failed_load_does_not_blank_a_stored_service_exe_path()
    {
        // The client file is read BEFORE the IPC call for exactly this reason: reading it afterwards
        // would leave the box empty on the error path, and the next Save would erase a working override.
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            string clientFile = Path.Combine(dir, "client-settings.json");
            ClientSettingsStore.Write(clientFile, ClientSettings.Default with { ServiceExecutablePath = exe });
            FakeIpcGateway gateway = new() { GetSettingsResult = new IpcError("SERVICE_UNAVAILABLE", "no pipe") };
            SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: clientFile);

            await vm.LoadAsync();
            Assert.Equal(exe, vm.ServiceExePath.Value);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(exe, ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Save_accepts_a_relative_service_exe_path_but_flags_it()
    {
        // Nothing about the exe path can refuse a save any more — a relative path simply never
        // resolves, so it is reported like any other unusable one.
        FakeIpcGateway gateway = new();
        string clientFile = TempFiles.ClientSettings();
        SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: clientFile);
        await vm.LoadAsync();

        vm.ServiceExePath.Value = @"..\FileManager.Service.exe";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Single(gateway.SaveSettingsCalls);           // the rest of the save went through
        Assert.Equal(@"..\FileManager.Service.exe", ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
        Assert.NotEqual(SettingNoticeSeverity.Info, vm.ServiceExePath.NoticeSeverity);
    }

    [Fact]
    public async Task Save_keeps_a_service_exe_path_with_no_file_and_says_what_it_falls_back_to()
    {
        // The reported bug, from the other end: saving a path that is not there must persist AND be
        // visibly flagged, naming whatever will actually be launched instead.
        string dir = NewDir();
        try
        {
            string fallback = RealExe(dir);
            string missing = Path.Combine(dir, "nope", "FileManager.Service.exe");
            string clientFile = Path.Combine(dir, "client-settings.json");
            FakeIpcGateway gateway = new();
            SettingsViewModel vm = new(gateway, new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            using (EnvOverride(fallback))
            {
                vm.ServiceExePath.Value = missing;
                await vm.SaveCommand.ExecuteAsync(null);
            }

            Assert.Single(gateway.SaveSettingsCalls);
            Assert.Equal(missing, ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
            Assert.Equal(SettingNoticeSeverity.Warning, vm.ServiceExePath.NoticeSeverity);
            Assert.Contains(fallback, vm.ServiceExePath.Notice);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Saving_a_wrong_executable_leaves_the_warning_on_screen()
    {
        // The exact complaint: a wrong executable saved with no visible sign, because the dialog closed
        // over the top of the note. The window no longer closes on save at all, so the note survives.
        string dir = NewDir();
        try
        {
            string wrongProgram = RealExe(dir, "SomethingElse.exe");
            string clientFile = Path.Combine(dir, "client-settings.json");
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = wrongProgram;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(wrongProgram, ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
            Assert.Equal(SettingNoticeSeverity.Warning, vm.ServiceExePath.NoticeSeverity);
            Assert.Contains("SomethingElse.exe", vm.ServiceExePath.Notice);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Save_accepts_a_blank_path_and_clears_a_stored_override()
    {
        string dir = NewDir();
        try
        {
            string clientFile = Path.Combine(dir, "client-settings.json");
            ClientSettingsStore.Write(clientFile,
                ClientSettings.Default with { ServiceExecutablePath = RealExe(dir) });
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = "   ";
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorMessage);
            Assert.Null(ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Save_completes_a_typed_folder_to_the_service_executable()
    {
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            string clientFile = Path.Combine(dir, "client-settings.json");
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = dir;      // the folder, not the file
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(exe, ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
            Assert.Equal(exe, vm.ServiceExePath.Value);      // and the box shows what was stored
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_valid_service_executable_saves_cleanly()
    {
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            string clientFile = Path.Combine(dir, "client-settings.json");
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            vm.ServiceExePath.Value = exe;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorMessage);
            Assert.Equal(SettingNoticeSeverity.Info, vm.ServiceExePath.NoticeSeverity);
            Assert.Equal(exe, ClientSettingsStore.Read(clientFile).ServiceExecutablePath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Save_does_not_touch_the_client_file_when_nothing_client_side_changed()
    {
        string dir = NewDir();
        try
        {
            string clientFile = Path.Combine(dir, "client-settings.json");
            SettingsViewModel vm = new(new FakeIpcGateway(), new FakeFolderPicker(), clientSettingsPath: clientFile);
            await vm.LoadAsync();

            vm.MaxScanThreads.Auto = false;     // a service-side edit only
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.False(File.Exists(clientFile));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Browse_fills_the_service_exe_path_and_opens_where_it_already_points()
    {
        string dir = NewDir();
        try
        {
            string exe = RealExe(dir);
            FakeFolderPicker picker = new() { FileResult = exe };
            SettingsViewModel vm = new(new FakeIpcGateway(), picker, clientSettingsPath: TempFiles.ClientSettings());
            vm.ServiceExePath.Value = @"D:\old\FileManager.Service.exe";

            await vm.BrowseServiceExePathCommand.ExecuteAsync(null);

            Assert.Equal(exe, vm.ServiceExePath.Value);
            Assert.Equal(@"D:\old\FileManager.Service.exe", picker.LastFileStartNear);
            Assert.Equal(["*.exe"], picker.LastFilePatterns);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
