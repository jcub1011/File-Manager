using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileManager.UI.Views
{
    /// <summary>Modal per-profile import confirmation returning the Import/Skip choice via
    /// ShowDialog&lt;bool&gt;. Skip is the default (Enter) — importing someone else's paths and
    /// dispositions is the choice that must be deliberate.</summary>
    public partial class ImportPreviewWindow : Window
    {
        public ImportPreviewWindow()
        {
            InitializeComponent();
        }

        private void OnImportClick(object? sender, RoutedEventArgs e) => Close(true);

        private void OnSkipClick(object? sender, RoutedEventArgs e) => Close(false);
    }
}
