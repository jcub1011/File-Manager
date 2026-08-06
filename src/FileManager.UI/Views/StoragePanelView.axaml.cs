using Avalonia.Controls;

namespace FileManager.UI.Views
{
    /// <summary>The per-volume storage forecast, shared by the Preview tab and the job queue's summary
    /// pane. Markup only — see StoragePanelView.axaml for why it was extracted.</summary>
    public partial class StoragePanelView : UserControl
    {
        public StoragePanelView()
        {
            InitializeComponent();
        }
    }
}
