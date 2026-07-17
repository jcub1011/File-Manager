using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FileManager.UI.ViewModels;
using System.Linq;
using System.Windows.Input;

namespace FileManager.UI.Views
{
    public partial class DryRunView : UserControl
    {
        // Keyboard shortcuts for the focused tree/list row. Parsed from the same strings shown as the
        // menu items' InputGesture hints, so the display and the handling can never drift apart.
        private static readonly KeyGesture CopyPathGesture = KeyGesture.Parse("Ctrl+C");
        private static readonly KeyGesture CopyNameGesture = KeyGesture.Parse("Alt+C");
        private static readonly KeyGesture ToggleFolderGesture = KeyGesture.Parse("Enter");
        private static readonly KeyGesture ToggleAllGesture = KeyGesture.Parse("Ctrl+Enter");
        private static readonly KeyGesture RevealGesture = KeyGesture.Parse("Ctrl+Space");
        private static readonly KeyGesture OpenFileGesture = KeyGesture.Parse("Shift+Space");

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

            // Row keyboard shortcuts (mirrors the context-menu actions). Tunnel/preview so we run
            // before the ListBox/TreeDataGrid bubble handlers and can claim Enter/Space; the handler
            // only acts when a tree/list row is the focus target, so Ctrl+C etc. in the search box
            // still behave normally.
            AddHandler(InputElement.KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
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

        private void OnRowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Source is not Avalonia.Visual source)
                return;

            // Tree rows: act on the SELECTED node (survives the row-list rebuild an expand/collapse
            // triggers), not the focused element — expanding recycles the focused row's container and
            // drops keyboard focus, so a focus-based lookup would break the very next keypress.
            if (source.FindAncestorOfType<TreeDataGrid>(includeSelf: true) is { } grid)
            {
                if (ResolveTreeNode(grid, source) is { } node)
                    HandleTreeNodeKey(node, e, grid);
                return;
            }

            // Flat list rows: single-select, so the current file is the ListBox's SelectedItem.
            if (source.FindAncestorOfType<ListBox>(includeSelf: true)?.SelectedItem is { } item)
                HandleRowKey(item, e);
        }

        private static DryRunTreeNode? ResolveTreeNode(TreeDataGrid grid, Avalonia.Visual source)
        {
            // Prefer the grid's selection; fall back to the focused row (e.g. focus present but nothing
            // selected yet).
            if (grid.Source is HierarchicalTreeDataGridSource<DryRunTreeNode> treeSource
                && treeSource.RowSelection?.SelectedItem is { } selected)
                return selected;
            return source.FindAncestorOfType<TreeDataGridRow>(includeSelf: true)?.DataContext as DryRunTreeNode;
        }

        private void HandleTreeNodeKey(DryRunTreeNode node, KeyEventArgs e, TreeDataGrid grid)
        {
            if (CopyPathGesture.Matches(e)) TryExecute(node.CopyPathCommand);
            else if (CopyNameGesture.Matches(e)) TryExecute(node.CopyNameCommand);
            else if (ToggleFolderGesture.Matches(e))
            {
                if (node is { IsDirectory: true, HasChildren: true })
                {
                    node.IsExpanded = !node.IsExpanded;
                    RestoreRowFocus(grid, node);
                }
            }
            else if (ToggleAllGesture.Matches(e))
            {
                if (node.IsDirectory)
                {
                    TryExecute(node.ToggleExpandCollapseAllCommand);
                    RestoreRowFocus(grid, node);
                }
            }
            else if (RevealGesture.Matches(e))
                TryExecute(node.IsDirectory ? node.OpenFolderInExplorerCommand : node.RevealInExplorerCommand);
            else if (OpenFileGesture.Matches(e))
            {
                if (!node.IsDirectory)
                    TryExecute(node.OpenFileCommand);
            }
            else
                return;

            e.Handled = true;   // claim our combos so the grid doesn't act on them
        }

        // An expand/collapse rebuilds the TreeDataGrid's flattened row list, recycling the focused
        // cell's container and dropping keyboard focus — so the next shortcut would land nowhere.
        // After the rebuild settles (Background priority runs it after layout), put focus back on the
        // folder's row so repeated Backspace / Ctrl+Backspace keep working. Keyboard focus in a
        // TreeDataGrid lives on a CELL, not the row (TreeDataGridCell is the focusable element, and
        // the grid's own FocusRow focuses a cell) — so focus the row's first focusable cell.
        private static void RestoreRowFocus(TreeDataGrid grid, DryRunTreeNode node) =>
            Dispatcher.UIThread.Post(() =>
            {
                TreeDataGridRow? row = grid.GetVisualDescendants().OfType<TreeDataGridRow>()
                    .FirstOrDefault(r => ReferenceEquals(r.DataContext, node));
                Control? cell = row?.GetVisualDescendants().OfType<TreeDataGridCell>()
                    .FirstOrDefault(c => c.Focusable);
                if (cell is not null)
                    cell.Focus();
                else
                    grid.Focus();
            }, DispatcherPriority.Background);

        private void HandleRowKey(object item, KeyEventArgs e)
        {
            // Flat list rows are always files; both row types expose the same command names.
            ICommand copyPath, copyName, openFile, reveal;
            switch (item)
            {
                case DryRunFileRow r:
                    (copyPath, copyName, openFile, reveal) =
                        (r.CopyPathCommand, r.CopyNameCommand, r.OpenFileCommand, r.RevealInExplorerCommand);
                    break;
                case DryRunDestinationRow r:
                    (copyPath, copyName, openFile, reveal) =
                        (r.CopyPathCommand, r.CopyNameCommand, r.OpenFileCommand, r.RevealInExplorerCommand);
                    break;
                default:
                    return;
            }

            if (CopyPathGesture.Matches(e)) TryExecute(copyPath);
            else if (CopyNameGesture.Matches(e)) TryExecute(copyName);
            else if (RevealGesture.Matches(e)) TryExecute(reveal);
            else if (OpenFileGesture.Matches(e)) TryExecute(openFile);
            else return;

            e.Handled = true;
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
