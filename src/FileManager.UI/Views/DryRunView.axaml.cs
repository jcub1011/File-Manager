using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Views
{
    public partial class DryRunView : UserControl
    {
        public DryRunView()
        {
            InitializeComponent();

            // Double-click a folder row to toggle its expansion (the grid only toggles on the chevron
            // otherwise). The tree cells aren't editable, so TreeDataGridCell's own DoubleTapped handler
            // leaves the event unhandled and it bubbles up to here. The clicked row is realized, so
            // setting the node's IsExpanded propagates to the grid via the expander column's two-way
            // binding — the same path the folder context menu's Open/Close Folder commands use.
            AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
        }

        private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is Visual source &&
                source.FindAncestorOfType<TreeDataGridRow>(includeSelf: true) is
                    { DataContext: DryRunTreeNode { IsDirectory: true, HasChildren: true } node })
            {
                node.IsExpanded = !node.IsExpanded;
                e.Handled = true;
            }
        }
    }
}
