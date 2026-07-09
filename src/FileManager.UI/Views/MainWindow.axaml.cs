using Avalonia.Controls;
using FileManager.UI.ViewModels;
using System;

namespace FileManager.UI.Views
{
    public partial class MainWindow : Window
    {
        // Close is a two-pass affair: the first OnClosing cancels, runs async teardown (job warning +
        // service shutdown), and only re-invokes Close() once the VM allows it.
        private bool _closeConfirmed;

        public MainWindow()
        {
            InitializeComponent();
        }

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (_closeConfirmed)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            try
            {
                bool canClose = DataContext is not MainWindowViewModel vm || await vm.RequestCloseAsync();
                if (canClose)
                {
                    _closeConfirmed = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                // Last resort: a failure here must never trap the user — log and force the close.
                Serilog.Log.Error(ex, "Window closing handler failed; forcing close");
                _closeConfirmed = true;
                Close();
            }
        }
    }
}
