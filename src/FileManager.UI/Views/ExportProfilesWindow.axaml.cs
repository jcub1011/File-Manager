using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileManager.UI.Views
{
    public partial class ExportProfilesWindow : Window
    {
        public ExportProfilesWindow()
        {
            InitializeComponent();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
