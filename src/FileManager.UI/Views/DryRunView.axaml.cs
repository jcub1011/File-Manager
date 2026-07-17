using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FileManager.UI.ViewModels;
using System.Windows.Input;

namespace FileManager.UI.Views
{
    public partial class DryRunView : UserControl
    {
        public DryRunView()
        {
            InitializeComponent();

            // Double-click behaviour for the report rows, handled here because the event bubbles up
            // unhandled from the (non-editable) tree cells and the list items:
            //   • a folder toggles its expansion (the grid only toggles on the chevron otherwise);
            //   • a file opens in its default application — the same action as the row's Open File
            //     menu item, so it is disabled (a no-op) for a planned file not yet on disk.
            // A clicked folder row is realized, so setting the node's IsExpanded propagates to the
            // grid via the expander column's two-way binding.
            AddHandler(InputElement.DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
        }

        private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is not Avalonia.Visual source)
                return;

            // Tree rows (both the Sources and Destinations trees): folders expand/collapse, files open.
            if (source.FindAncestorOfType<TreeDataGridRow>(includeSelf: true) is { DataContext: DryRunTreeNode node })
            {
                if (node is { IsDirectory: true, HasChildren: true })
                    node.IsExpanded = !node.IsExpanded;
                else if (!node.IsDirectory)
                    TryExecute(node.OpenFileCommand);
                e.Handled = true;
                return;
            }

            // Flat list rows: open the file in its default application. Both row types expose the same
            // OpenFileCommand (guarded by a File.Exists check).
            if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } dataContext })
            {
                ICommand? open = dataContext switch
                {
                    DryRunFileRow row => row.OpenFileCommand,
                    DryRunDestinationRow row => row.OpenFileCommand,
                    _ => null,
                };
                if (open is not null && TryExecute(open))
                    e.Handled = true;
            }
        }

        private static bool TryExecute(ICommand command)
        {
            if (!command.CanExecute(null))
                return false;
            command.Execute(null);
            return true;
        }
    }
}
