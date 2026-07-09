using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileManager.UI.Views
{
    /// <summary>Minimal modal Yes/No confirmation returning a bool via ShowDialog&lt;bool&gt;. The repo
    /// has no reusable message box, so this is the tiny shared one for close-time confirmations.</summary>
    public partial class ConfirmWindow : Window
    {
        public ConfirmWindow()
        {
            InitializeComponent();
        }

        public ConfirmWindow(string message) : this()
        {
            MessageText.Text = message;
        }

        private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);

        private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);
    }
}
