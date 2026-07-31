using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FileManager.UI.Services;
using FileManager.UI.ViewModels;
using FileManager.UI.Views;
using System;
using System.Threading;

namespace FileManager.UI
{
    public partial class App : Application
    {
        private readonly CancellationTokenSource _shutdown = new();
        private IpcGateway? _gateway;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Apply the persisted theme BEFORE the window is shown (below), so the first paint
                // uses the correct variant instead of App.axaml's "Default" — otherwise the window
                // flashes light before the theme lands. Read synchronously from settings.json on disk
                // rather than over IPC: the IPC round-trip may launch the service and is far too slow
                // for the first frame. The service's value reconciles later in window.Opened.
                ThemeApplier.Apply(StartupTheme.Read());

                // Manual composition — no DI container; the object graph is four services deep.
                _gateway = new IpcGateway();
                MainWindow window = new();
                StorageProviderFolderPicker folderPicker = new(window);
                LogFolderService logFolder = new();
                DryRunItemActions dryRunActions = new(window);
                SystemDrives systemDrives = new();
                MainWindowViewModel viewModel = new(
                    _gateway, folderPicker, logFolder, dryRunActions, systemDrives: systemDrives);
                viewModel.ShowSettingsDialog = async settings =>
                {
                    SettingsWindow dialog = new() { DataContext = settings };
                    settings.RequestClose = dialog.Close;
                    // The move-profiles prompt is modal on the settings dialog and defaults to No
                    // (the ConfirmWindow's No button is IsDefault).
                    settings.ConfirmMoveProfiles = async message =>
                        await new ConfirmWindow(message, "Move profiles", "Don't move", confirmIsDanger: false)
                            .ShowDialog<bool>(dialog);
                    await dialog.ShowDialog(window);
                };
                viewModel.ShowExportDialog = async export =>
                {
                    ExportProfilesWindow dialog = new() { DataContext = export };
                    export.RequestClose = dialog.Close;
                    await dialog.ShowDialog(window);
                };
                viewModel.ConfirmImport = async preview =>
                    await new ImportPreviewWindow { DataContext = preview }.ShowDialog<bool>(window);
                viewModel.ConfirmClose = async message =>
                    await new ConfirmWindow(message).ShowDialog<bool>(window);
                // A manual run moves real files and applies the profile's source disposition, so it
                // always confirms, danger-styled — the same friction the close-time warning uses.
                viewModel.ConfirmRunProfile = async message =>
                    await new ConfirmWindow(message, "Run now", "Cancel", confirmIsDanger: true)
                        .ShowDialog<bool>(window);
                // Modal rather than an inline bar so the same confirmation appears whichever entry
                // point asked (Profile tab button, list row menu, collapsed rail menu).
                viewModel.ConfirmDeleteProfile = async message =>
                    await new ConfirmWindow(message, "Delete", "Cancel", confirmIsDanger: true)
                        .ShowDialog<bool>(window);
                window.DataContext = viewModel;
                desktop.MainWindow = window;

                // Started from the UI thread, and the pump uses no ConfigureAwait(false) — so every
                // event handler (and therefore every view-model mutation) resumes on the UI thread
                // (§8), with no Dispatcher marshalling. Connected re-seeds on each attempt because the
                // service's event delivery is bounded and drop-oldest.
                EngineEventPump pump = new(_gateway)
                {
                    Event = viewModel.HandleEngineEvent,
                    Connected = viewModel.ReconcileEngineStateAsync,
                };

                window.Opened += async (_, _) =>
                {
                    try
                    {
                        await viewModel.InitializeAsync();
                        // Reconcile with the service's authoritative settings. The theme was already
                        // applied synchronously from disk before the window was shown; this re-applies
                        // from the source of truth in case the on-disk copy was stale or unreadable.
                        var settingsResult = await _gateway.GetSettingsAsync();
                        if (settingsResult.TryGetValue(out FileManager.Contracts.Settings.GlobalSettings? settings))
                            ThemeApplier.Apply(settings.ThemeMode);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "Startup failed");
                        viewModel.List.ErrorMessage = $"Startup failed: {ex.Message}";
                    }
                    finally
                    {
                        // AFTER InitializeAsync, and in a finally so a failed profile load still gets
                        // live events. The pump's first act is a reconcile, and each activity row
                        // resolves its profile name through the shell's loaded profile list — starting
                        // the pump before that list existed left every row of the first seed nameless.
                        _ = pump.RunAsync(_shutdown.Token);
                    }
                };
                _ = viewModel.StatusBar.RunPollLoopAsync(_shutdown.Token);

                desktop.Exit += (_, _) =>
                {
                    _shutdown.Cancel();
                    _gateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
