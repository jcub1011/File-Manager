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
        private IDisposable? _previewAgeTicker;

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
                // flashes light before the theme lands. The theme is client-side state, so this is a
                // local file read with no service involved and nothing to reconcile later.
                ThemeApplier.Apply(StartupTheme.Read());

                // Manual composition — no DI container; the object graph is four services deep.
                // The gateway re-reads the configured service executable path on every connect attempt
                // (not once here), so a path corrected in the settings window takes effect immediately.
                _gateway = new IpcGateway(ClientSettingsStore.ReadServiceExecutablePath);
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
                    // The move-profiles prompt is modal on the settings dialog and defaults to No
                    // (the ConfirmWindow's No button is IsDefault).
                    settings.ConfirmMoveProfiles = async message =>
                        await new ConfirmWindow(message, "Move profiles", "Don't move", confirmIsDanger: false)
                            .ShowDialog<bool>(dialog);
                    // Danger-styled: stopping a service can interrupt jobs that are moving real files,
                    // the same friction "Run now" and the close-time warning use.
                    settings.ConfirmStopPreviousService = async message =>
                        await new ConfirmWindow(message, "Stop it", "Keep running", confirmIsDanger: true)
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
                // No run confirmation dialogs: a run's planning phase touches nothing, and the Preview
                // tab's footer is where the user sees the actual rows and approves them. A modal showing
                // counts was standing in for that view; now that the view exists, it is the friction.
                // Modal rather than an inline bar so the same confirmation appears whichever entry
                // point asked (Profile tab button, list row menu, collapsed rail menu).
                viewModel.ConfirmDeleteProfile = async message =>
                    await new ConfirmWindow(message, "Delete", "Cancel", confirmIsDanger: true)
                        .ShowDialog<bool>(window);

                // The ONE non-modal window in the app, and deliberately so: a queue you must dismiss before
                // you can edit a profile is a dialog, not a queue. Show(owner) keeps it above the main
                // window and closing with it, without blocking input to it.
                //
                // The instance is tracked so a second press focuses the window already open rather than
                // stacking duplicates — there is no existing precedent for this because every other window
                // here is modal and cannot be opened twice.
                JobQueueWindow? queue = null;
                viewModel.ShowJobQueue = () =>
                {
                    if (queue is not null)
                    {
                        queue.Activate();
                        return;
                    }
                    // DataContext is the SHELL's view model, not a fresh one: the queue keeps consuming
                    // engine events while this window is closed, so reopening shows current state instead of
                    // starting empty.
                    queue = new JobQueueWindow { DataContext = viewModel.Queue };
                    queue.Closed += (_, _) => queue = null;
                    queue.Show(window);
                };
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
                        // No theme reconcile over IPC: the client-settings file applied above IS the
                        // source of truth for the theme now, so there is nothing authoritative to
                        // catch up with — and nothing that has to wait for a reachable service.
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
                        // Ages the retained preview's caption once a minute, so "Previewed 3 min ago"
                        // advances and the staleness banner appears without the user touching anything.
                        _previewAgeTicker = viewModel.StartPreviewAgeTicker();
                        // The idle baseline every later sample is read against: posted at Background
                        // priority so it runs once the first paint, the font atlases and the profile
                        // list are done, rather than mid-startup.
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            static () => UiMemoryLog.Sample("idle"),
                            Avalonia.Threading.DispatcherPriority.Background);
                    }
                };
                _ = viewModel.StatusBar.RunPollLoopAsync(_shutdown.Token);

                desktop.Exit += (_, _) =>
                {
                    _shutdown.Cancel();
                    _previewAgeTicker?.Dispose();
                    _gateway?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
