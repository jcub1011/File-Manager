using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileManager.UI.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
