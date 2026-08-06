using Avalonia.Controls;

namespace FileManager.UI.Views
{
    /// <summary>The job queue's summary pane for the selected run, plus the footer that acts on it.
    /// Markup only — its DataContext is the JobQueueViewModel, supplied by the window.</summary>
    public partial class RunSummaryPane : UserControl
    {
        public RunSummaryPane()
        {
            InitializeComponent();
        }
    }
}
