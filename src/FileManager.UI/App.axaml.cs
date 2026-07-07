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
                MainWindowViewModel viewModel = new(_gateway, folderPicker);
                window.DataContext = viewModel;
                desktop.MainWindow = window;

                window.Opened += async (_, _) =>
                {
                    try
                    {
                        await viewModel.InitializeAsync();
                    }
                    catch (Exception ex)
                    {
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
