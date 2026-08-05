using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileManager.UI.Views
{
    /// <summary>The non-modal job queue. Shown with <c>Show(owner)</c> rather than <c>ShowDialog</c> — see
    /// the composition root, which also owns focusing the instance already open instead of making a second.
    /// <para>Closing it only hides the queue: the view model is owned by the shell and keeps consuming
    /// engine events, so reopening shows current state rather than starting from empty.</para></summary>
    public partial class JobQueueWindow : Window
    {
        public JobQueueWindow()
        {
            InitializeComponent();
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
