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
                // Manual composition — no DI container; the object graph is four services deep.
                _gateway = new IpcGateway();
                MainWindow window = new();
                StorageProviderFolderPicker folderPicker = new(window);
                LogFolderService logFolder = new();
                MainWindowViewModel viewModel = new(_gateway, folderPicker, logFolder);
                viewModel.ShowSettingsDialog = async settings =>
                {
                    SettingsWindow dialog = new() { DataContext = settings };
                    settings.RequestClose = dialog.Close;
                    await dialog.ShowDialog(window);
                };
                viewModel.ConfirmClose = async message =>
                    await new ConfirmWindow(message).ShowDialog<bool>(window);
                window.DataContext = viewModel;
                desktop.MainWindow = window;

                window.Opened += async (_, _) =>
                {
                    try
                    {
                        await viewModel.InitializeAsync();
                        // Apply the persisted theme once settings can be read. Left until now (rather
                        // than App.axaml) because it comes from the service over IPC; App.axaml's
                        // "Default" variant is the correct pre-connect value for ThemeMode.System.
                        var settingsResult = await _gateway.GetSettingsAsync();
                        if (settingsResult.TryGetValue(out FileManager.Contracts.Settings.GlobalSettings? settings))
                            ThemeApplier.Apply(settings.ThemeMode);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "Startup failed");
                        viewModel.List.ErrorMessage = $"Startup failed: {ex.Message}";
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
