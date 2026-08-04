using Avalonia.Controls;

namespace FileManager.UI.Views
{
    // No keyboard wiring here: undo/redo is scoped to the Profile TAB, not to this control, so the
    // gesture handler lives in MainWindow where the tab selection is known. Registering it here would
    // also miss Ctrl+Z pressed while focus sits in the sidebar, which is outside this subtree.
    public partial class ProfileEditorView : UserControl
    {
        public ProfileEditorView()
        {
            InitializeComponent();
        }
    }
}
