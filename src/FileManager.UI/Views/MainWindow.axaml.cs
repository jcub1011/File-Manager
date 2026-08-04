using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using FileManager.UI.Undo;
using FileManager.UI.ViewModels;
using System;

namespace FileManager.UI.Views
{
    public partial class MainWindow : Window
    {
        // Close is a two-pass affair: the first OnClosing cancels, runs async teardown (job warning +
        // service shutdown), and only re-invokes Close() once the VM allows it.
        private bool _closeConfirmed;

        // Guards the async teardown so it runs exactly once. OnClosing is async void, so a second
        // close attempt (e.g. a double-click) arriving while RequestCloseAsync is still awaiting would
        // otherwise re-enter with _closeConfirmed still false and fire a second service shutdown.
        private bool _closing;

        // The resizable profile sidebar column (column 0 of the content grid).
        private ColumnDefinition ProfilePanelColumn => ContentGrid.ColumnDefinitions[0];

        public MainWindow()
        {
            InitializeComponent();
            UpdateMaximizeGlyph();

            // Double-click the splitter to toggle the collapsed sidebar. GridSplitter marks pointer
            // input handled for dragging, so DoubleTapped may never fire — subscribe to PointerPressed
            // with handledEventsToo and detect the double via ClickCount. PointerReleased snaps/persists
            // the width the drag landed on.
            SidebarSplitter.AddHandler(PointerPressedEvent, OnSplitterPointerPressed,
                RoutingStrategies.Bubble, handledEventsToo: true);
            SidebarSplitter.AddHandler(PointerReleasedEvent, OnSplitterPointerReleased,
                RoutingStrategies.Bubble, handledEventsToo: true);

            // Live-switch collapsed/expanded as the splitter drags the column across the threshold.
            ProfilePanelColumn.GetObservable(ColumnDefinition.WidthProperty).Subscribe(OnSidebarWidthChanged);

            // Undo/redo for the profile draft is handled at window scope, not inside ProfileEditorView,
            // for two reasons: the shortcut has to work while focus is in the sidebar (which is outside
            // the editor's subtree), and the "am I in scope?" question is about which TAB is selected —
            // something only this window can answer.
            //
            // Tunnel (preview) routing, so the window sees Ctrl+Z before the focused TextBox does. That
            // is a deliberate trade, the same one the settings dialog makes: undo always means "undo the
            // last profile change" no matter which field has focus, at the cost of a TextBox's own
            // per-character undo. Coalescing makes a typing run one profile step, so reverting what was
            // just typed — in the multi-line glob boxes too — still takes one press.
            AddHandler(InputElement.KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel);

            // A field losing focus ends its coalescing run, so returning to it later starts a fresh undo
            // step instead of extending an edit the user has moved on from. It is also what makes a path
            // chosen through Browse its own step: clicking the button moves focus off the box.
            AddHandler(InputElement.LostFocusEvent, OnEditorLostFocus, RoutingStrategies.Tunnel);
        }

        /// <summary>Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, scoped to the profile draft on screen.
        ///
        /// The gate is the point of this method. The editor view model is reused for every profile and
        /// its undo steps capture it directly, so undo must never be reachable from a context where the
        /// user is not looking at that draft — the Dry Run tab shows a preview of a run, and stepping the
        /// profile backwards from there would be an edit the user cannot see happening.</summary>
        private void OnUndoRedoKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;
            // The tab decides whether the draft is on screen at all. HasProfile is belt-and-braces for the
            // case where it is the selected tab but nothing is loaded (the document area is hidden then,
            // while ProfileTab.IsSelected stays true) — the history is already empty there.
            if (!ProfileTab.IsSelected || !vm.Editor.HasProfile)
                return;
            UndoGestures.TryHandle(vm.Editor.History, e);
        }

        /// <summary>Ends the coalescing run on any focus change in the window. Deliberately NOT gated on
        /// the active tab: breaking a run is always safe, whereas gating it would depend on whether the
        /// tab-selection change or the focus change lands first when the user clicks straight from a text
        /// box onto the Dry Run tab header.</summary>
        private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.Editor.History.BreakMerge();
        }

        // Live preview during a drag: the current width decides the mode (below the min expanded width
        // shows the rail, at or above it shows the full list). Persistence and snapping happen on
        // release; this only flips the content and remembers the last expanded width, so it stays
        // idempotent under the programmatic width sets from load/double-click/snap.
        private void OnSidebarWidthChanged(GridLength width)
        {
            if (!width.IsAbsolute || DataContext is not MainWindowViewModel vm)
                return;

            if (width.Value < MainWindowViewModel.MinExpandedSidebarWidth)
            {
                vm.SidebarCollapsed = true;
            }
            else
            {
                vm.SidebarCollapsed = false;
                vm.SidebarExpandedWidth = width.Value;
            }
        }

        private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            if (ProfilePanelColumn.Width.Value < MainWindowViewModel.MinExpandedSidebarWidth)
            {
                // Landed below the threshold: settle on a clean rail width.
                vm.SidebarCollapsed = true;
                ProfilePanelColumn.Width = new GridLength(MainWindowViewModel.CollapsedSidebarWidth);
            }
            else
            {
                vm.SidebarCollapsed = false;
                vm.SidebarExpandedWidth = ProfilePanelColumn.Width.Value;
            }
            vm.SaveSidebarState();
        }

        // Apply the persisted sidebar layout once the template + DataContext are in place.
        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (DataContext is MainWindowViewModel vm)
                ApplyCollapseState(vm.SidebarCollapsed, vm);
        }

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.ClickCount == 2 && DataContext is MainWindowViewModel vm)
            {
                bool collapsing = !vm.SidebarCollapsed;
                // Remember the width we're leaving so expanding restores it.
                if (collapsing)
                    vm.SidebarExpandedWidth = ProfilePanelColumn.Width.Value;

                vm.SidebarCollapsed = collapsing;
                ApplyCollapseState(collapsing, vm);
                vm.SaveSidebarState();
                e.Handled = true;
            }
        }

        // Drive the sidebar column width from the collapsed flag (used on load and double-click). The
        // column stays freely draggable down to the rail width — the drag watcher/release handler do
        // the mode switching — so this only sets the width to the rail (collapsed) or the last
        // expanded width. The width watcher keeps the VM flag in step with whatever we set here.
        private void ApplyCollapseState(bool collapsed, MainWindowViewModel vm)
        {
            ProfilePanelColumn.Width = new GridLength(
                collapsed ? MainWindowViewModel.CollapsedSidebarWidth : vm.SidebarExpandedWidth);
        }

        // WindowDecorations="None" means we draw the caption buttons, so their actions are wired here
        // rather than by the framework. Close() routes through the two-pass OnClosing teardown below.
        private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClose(object? sender, RoutedEventArgs e) => Close();

        // Keep the maximize/restore glyph and tooltip in sync with the window state — it also changes
        // when the user double-clicks the title bar (native ElementRole="TitleBar" behaviour).
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty)
                UpdateMaximizeGlyph();
        }

        private void UpdateMaximizeGlyph()
        {
            // Called from the constructor before the named fields are assigned during early property
            // changes; guard until the template has populated them.
            if (MaximizeIcon is null || MaximizeButton is null)
                return;

            bool maximized = WindowState == WindowState.Maximized;
            if (this.TryFindResource(maximized ? "IconRestore" : "IconMaximize", out object? geometry)
                && geometry is Geometry g)
                MaximizeIcon.Data = g;
            ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
        }

        // NOTE: this override must stay `async void` — Avalonia's OnClosing returns void and the
        // framework does not await it, so `async Task` would not compile as an override and its
        // exceptions would go unobserved. Because an exception escaping an async void crashes the
        // process, the ENTIRE body is wrapped in try/catch; nothing may throw outside it.
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                // Persist the latest expanded width the user dragged to (the splitter mutates the
                // column directly, so read it back here). Harmless to repeat on the confirmed pass.
                if (DataContext is MainWindowViewModel sidebarVm && !sidebarVm.SidebarCollapsed)
                {
                    sidebarVm.SidebarExpandedWidth = ProfilePanelColumn.Width.Value;
                    sidebarVm.SaveSidebarState();
                }

                if (_closeConfirmed)
                {
                    base.OnClosing(e);
                    return;
                }

                // Always cancel this pass; teardown decides whether to re-invoke Close(). A re-entrant
                // call while the first teardown is still running is cancelled and dropped here.
                e.Cancel = true;
                if (_closing)
                    return;
                _closing = true;

                bool canClose = DataContext is not MainWindowViewModel vm || await vm.RequestCloseAsync();
                if (canClose)
                {
                    _closeConfirmed = true;
                    Close();
                }
                else
                {
                    // The close was vetoed (e.g. user cancelled the active-jobs warning); allow a
                    // later close attempt to run teardown again.
                    _closing = false;
                }
            }
            catch (Exception ex)
            {
                // Last resort: a failure here must never trap the user — and an async void must never
                // let an exception escape and crash the app. Log and force the close.
                Serilog.Log.Error(ex, "Window closing handler failed; forcing close");
                _closeConfirmed = true;
                Close();
            }
        }
    }
}
