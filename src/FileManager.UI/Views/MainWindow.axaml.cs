using Avalonia.Controls;
using Avalonia.Input;
using FileManager.UI.ViewModels;
using System;

namespace FileManager.UI.Views
{
    public partial class MainWindow : Window
    {
        // Close is a two-pass affair: the first OnClosing cancels, runs async teardown (job warning +
        // service shutdown), and only re-invokes Close() once the VM allows it.
        private bool _closeConfirmed;

        // Guards the async teardown so it runs exactly once. OnClosing is async void, so a second
        // close attempt (e.g. a double-click) arriving while RequestCloseAsync is still awaiting would
        // otherwise re-enter with _closeConfirmed still false and fire a second service shutdown.
        private bool _closing;

        public MainWindow()
        {
            InitializeComponent();

            // The client area is extended under the OS title bar (ExtendClientAreaToDecorationsHint),
            // so the custom title-bar grid must drive window move/maximize itself.
            TitleBar.PointerPressed += OnTitleBarPointerPressed;
            TitleBar.DoubleTapped += OnTitleBarDoubleTapped;
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        // NOTE: this override must stay `async void` — Avalonia's OnClosing returns void and the
        // framework does not await it, so `async Task` would not compile as an override and its
        // exceptions would go unobserved. Because an exception escaping an async void crashes the
        // process, the ENTIRE body is wrapped in try/catch; nothing may throw outside it.
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                if (_closeConfirmed)
                {
                    base.OnClosing(e);
                    return;
                }

                // Always cancel this pass; teardown decides whether to re-invoke Close(). A re-entrant
                // call while the first teardown is still running is cancelled and dropped here.
                e.Cancel = true;
                if (_closing)
                    return;
                _closing = true;

                bool canClose = DataContext is not MainWindowViewModel vm || await vm.RequestCloseAsync();
                if (canClose)
                {
                    _closeConfirmed = true;
                    Close();
                }
                else
                {
                    // The close was vetoed (e.g. user cancelled the active-jobs warning); allow a
                    // later close attempt to run teardown again.
                    _closing = false;
                }
            }
            catch (Exception ex)
            {
                // Last resort: a failure here must never trap the user — and an async void must never
                // let an exception escape and crash the app. Log and force the close.
                Serilog.Log.Error(ex, "Window closing handler failed; forcing close");
                _closeConfirmed = true;
                Close();
            }
        }
    }
}
